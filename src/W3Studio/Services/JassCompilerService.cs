using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using W3Studio.Models;

namespace W3Studio.Services;

public class ValidationResult
{
    public bool Success => ErrorCount == 0;
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public double ElapsedSeconds { get; set; }
    public string CompiledScript { get; set; } = string.Empty;
    public List<CompilerDiagnostic> Diagnostics { get; } = new();
}

public class JassCompilerService
{
    public async Task<ValidationResult> ValidateAndCompileAsync(
        string scriptText,
        WorkspaceSettings settings,
        string sourceLabel = "war3map.j")
    {
        var sw = Stopwatch.StartNew();
        var result = new ValidationResult { CompiledScript = scriptText };

        await Task.Run(() =>
        {
            // 1. Built-in 1.27b Compatibility & Structural Analysis
            Run127bCompatibilityChecks(scriptText, sourceLabel, result);

            // 2. Check if script contains uncompiled vJASS constructs
            var hasVjassConstructs = Regex.IsMatch(
                scriptText,
                @"\b(library|endlibrary|scope|endscope|struct|endstruct|method|endmethod|textmacro|runtextmacro)\b|//!\s*(import|external|textmacro|runtextmacro|zinc)",
                RegexOptions.Multiline);

            var tempDir = Path.Combine(Path.GetTempPath(), "W3Studio_Compile");
            Directory.CreateDirectory(tempDir);
            var inputScriptPath = Path.Combine(tempDir, "war3map_input.j");
            var outputScriptPath = Path.Combine(tempDir, "war3map_output.j");
            File.WriteAllText(inputScriptPath, scriptText, new UTF8Encoding(false));

            var commonJ = settings.CommonJPath;
            var blizzardJ = settings.BlizzardJPath;

            if (hasVjassConstructs && File.Exists(settings.CliJassHelperPath) && File.Exists(commonJ) && File.Exists(blizzardJ))
            {
                RunCliJassHelper(settings, commonJ, blizzardJ, inputScriptPath, outputScriptPath, sourceLabel, result);
            }
            else if (File.Exists(settings.PjassPath) && File.Exists(commonJ) && File.Exists(blizzardJ))
            {
                RunPjass(settings.PjassPath, commonJ, blizzardJ, inputScriptPath, sourceLabel, result);
            }
            else
            {
                result.Diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Info",
                    Line = 1,
                    Source = "Analizador 1.27b",
                    TriggerOrFile = sourceLabel,
                    Message = "Validación estructural y de compatibilidad 1.27b completada en memoria."
                });
            }
        });

        sw.Stop();
        result.ElapsedSeconds = sw.Elapsed.TotalSeconds;

        if (result.ErrorCount == 0 && result.Diagnostics.Count == 0)
        {
            result.Diagnostics.Add(new CompilerDiagnostic
            {
                Severity = "Info",
                Line = 1,
                Source = "JassHelper / PJass",
                TriggerOrFile = sourceLabel,
                Message = $"Compilación y validación 1.27b exitosa (0 errores en {result.ElapsedSeconds:F2}s)."
            });
        }

        return result;
    }

    private static string StripLineCommentRespectingStrings(string rawLine)
    {
        bool inString = false;
        for (int i = 0; i < rawLine.Length; i++)
        {
            char c = rawLine[i];
            if (c == '"' && (i == 0 || rawLine[i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (!inString && c == '/' && i + 1 < rawLine.Length && rawLine[i + 1] == '/')
            {
                return rawLine.Substring(0, i);
            }
        }
        return rawLine;
    }

    private static void Run127bCompatibilityChecks(string scriptText, string sourceLabel, ValidationResult result)
    {
        var lines = scriptText.Split('\n');
        var openFuncLine = 0;
        var inFunction = false;
        var seenExecutableInFunction = false;
        var openGlobalsLine = 0;
        var ifStack = new Stack<int>();
        var loopStack = new Stack<int>();

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNum = i + 1;
            var rawLine = lines[i];
            var trimmedRaw = rawLine.TrimStart();
            if (trimmedRaw.StartsWith("//!", StringComparison.Ordinal))
                continue;

            var codePart = StripLineCommentRespectingStrings(rawLine).Trim();

            if (string.IsNullOrEmpty(codePart))
                continue;

            // Regla Crítica 1.27b: Prohibido usar nativas Blz* de 1.29+ / Reforged
            var blzMatch = Regex.Match(codePart, @"\b(Blz[A-Za-z0-9_]+)\s*\(");
            if (blzMatch.Success)
            {
                result.ErrorCount++;
                result.Diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Error",
                    Line = lineNum,
                    Source = "Regla 1.27b",
                    TriggerOrFile = sourceLabel,
                    Message = $"Función incompatible con 1.27b detectada: '{blzMatch.Groups[1].Value}' pertenece a 1.29+/Reforged."
                });
            }

            if (Regex.IsMatch(codePart, @"^globals\b"))
            {
                if (openGlobalsLine > 0)
                {
                    result.ErrorCount++;
                    result.Diagnostics.Add(new CompilerDiagnostic
                    {
                        Severity = "Error",
                        Line = lineNum,
                        Source = "JASS 1.27b",
                        TriggerOrFile = sourceLabel,
                        Message = $"Bloque 'globals' anidado (ya había uno abierto en la línea {openGlobalsLine})."
                    });
                }
                else
                {
                    openGlobalsLine = lineNum;
                }
            }
            else if (Regex.IsMatch(codePart, @"^endglobals\b"))
            {
                if (openGlobalsLine == 0)
                {
                    result.ErrorCount++;
                    result.Diagnostics.Add(new CompilerDiagnostic
                    {
                        Severity = "Error",
                        Line = lineNum,
                        Source = "JASS 1.27b",
                        TriggerOrFile = sourceLabel,
                        Message = "'endglobals' sin un bloque 'globals' abierto."
                    });
                }
                else
                {
                    openGlobalsLine = 0;
                }
            }

            if (Regex.IsMatch(codePart, @"^(?:public\s+|private\s+|static\s+)*(?:function|method)\b"))
            {
                inFunction = true;
                seenExecutableInFunction = false;
                openFuncLine = lineNum;
            }
            else if (Regex.IsMatch(codePart, @"^(?:endfunction|endmethod)\b"))
            {
                inFunction = false;
                seenExecutableInFunction = false;
            }
            else if (inFunction)
            {
                if (Regex.IsMatch(codePart, @"^if\b"))
                {
                    ifStack.Push(lineNum);
                }
                else if (Regex.IsMatch(codePart, @"^endif\b"))
                {
                    if (ifStack.Count > 0) ifStack.Pop();
                    else
                    {
                        result.ErrorCount++;
                        result.Diagnostics.Add(new CompilerDiagnostic
                        {
                            Severity = "Error",
                            Line = lineNum,
                            Source = "JASS 1.27b",
                            TriggerOrFile = sourceLabel,
                            Message = "'endif' sin un bloque 'if' correspondiente."
                        });
                    }
                }
                else if (Regex.IsMatch(codePart, @"^loop\b"))
                {
                    loopStack.Push(lineNum);
                }
                else if (Regex.IsMatch(codePart, @"^endloop\b"))
                {
                    if (loopStack.Count > 0) loopStack.Pop();
                    else
                    {
                        result.ErrorCount++;
                        result.Diagnostics.Add(new CompilerDiagnostic
                        {
                            Severity = "Error",
                            Line = lineNum,
                            Source = "JASS 1.27b",
                            TriggerOrFile = sourceLabel,
                            Message = "'endloop' sin un bloque 'loop' correspondiente."
                        });
                    }
                }

                if (Regex.IsMatch(codePart, @"^local\s+"))
                {
                    if (seenExecutableInFunction)
                    {
                        result.ErrorCount++;
                        result.Diagnostics.Add(new CompilerDiagnostic
                        {
                            Severity = "Error",
                            Line = lineNum,
                            Source = "JASS 1.27b",
                            TriggerOrFile = sourceLabel,
                            Message = "Declaración 'local' después de una instrucción ejecutable (en JASS todas las locales deben ir al inicio de la función)."
                        });
                    }
                }
                else if (Regex.IsMatch(codePart, @"^(?:set|call|if|loop|return|exitwhen)\b"))
                {
                    seenExecutableInFunction = true;
                }
            }
        }

        if (openGlobalsLine > 0)
        {
            result.ErrorCount++;
            result.Diagnostics.Add(new CompilerDiagnostic
            {
                Severity = "Error",
                Line = openGlobalsLine,
                Source = "JASS 1.27b",
                TriggerOrFile = sourceLabel,
                Message = $"Bloque 'globals' abierto en la línea {openGlobalsLine} sin 'endglobals'."
            });
        }

        if (ifStack.Count > 0)
        {
            int unclosedIf = ifStack.Pop();
            result.ErrorCount++;
            result.Diagnostics.Add(new CompilerDiagnostic
            {
                Severity = "Error",
                Line = unclosedIf,
                Source = "JASS 1.27b",
                TriggerOrFile = sourceLabel,
                Message = $"Bloque 'if' abierto en la línea {unclosedIf} sin 'endif'."
            });
        }

        if (loopStack.Count > 0)
        {
            int unclosedLoop = loopStack.Pop();
            result.ErrorCount++;
            result.Diagnostics.Add(new CompilerDiagnostic
            {
                Severity = "Error",
                Line = unclosedLoop,
                Source = "JASS 1.27b",
                TriggerOrFile = sourceLabel,
                Message = $"Bloque 'loop' abierto en la línea {unclosedLoop} sin 'endloop'."
            });
        }

        if (inFunction && openFuncLine > 0)
        {
            result.ErrorCount++;
            result.Diagnostics.Add(new CompilerDiagnostic
            {
                Severity = "Error",
                Line = openFuncLine,
                Source = "JASS 1.27b",
                TriggerOrFile = sourceLabel,
                Message = $"Bloque 'function/method' abierto en la línea {openFuncLine} sin su correspondiente 'endfunction/endmethod'."
            });
        }
    }

    private static void RunCliJassHelper(
        WorkspaceSettings settings,
        string commonJ,
        string blizzardJ,
        string inputScriptPath,
        string outputScriptPath,
        string sourceLabel,
        ValidationResult result)
    {
        try
        {
            File.Copy(inputScriptPath, outputScriptPath, overwrite: true);
            var psi = new ProcessStartInfo
            {
                FileName = settings.CliJassHelperPath,
                Arguments = $"--scriptonly \"{commonJ}\" \"{blizzardJ}\" \"{inputScriptPath}\" \"{outputScriptPath}\"",
                WorkingDirectory = settings.BundledToolsDir,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return;

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);

            var combined = (stdout + "\n" + stderr).Trim();
            if (proc.ExitCode == 0 && File.Exists(outputScriptPath))
            {
                result.CompiledScript = File.ReadAllText(outputScriptPath, Encoding.UTF8);
                result.Diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Info",
                    Line = 1,
                    Source = "clijasshelper",
                    TriggerOrFile = sourceLabel,
                    Message = "Compilación vJASS completada con éxito (clijasshelper.exe)."
                });
            }
            else
            {
                int prevErrors = result.ErrorCount;
                if (!string.IsNullOrWhiteSpace(combined))
                    ParseCompilerOutput(combined, "clijasshelper", sourceLabel, result);
                if (result.ErrorCount == prevErrors)
                {
                    result.ErrorCount++;
                    result.Diagnostics.Add(new CompilerDiagnostic
                    {
                        Severity = "Error",
                        Line = 1,
                        Source = "clijasshelper",
                        TriggerOrFile = sourceLabel,
                        Message = string.IsNullOrWhiteSpace(combined)
                            ? $"clijasshelper.exe falló con código de salida {proc.ExitCode}."
                            : combined
                    });
                }
            }
        }
        catch (Exception ex)
        {
            result.WarningCount++;
            result.Diagnostics.Add(new CompilerDiagnostic
            {
                Severity = "Warning",
                Line = 1,
                Source = "clijasshelper",
                TriggerOrFile = sourceLabel,
                Message = $"No se pudo invocar clijasshelper.exe: {ex.Message}"
            });
        }
    }

    private static void RunPjass(
        string pjassPath,
        string commonJ,
        string blizzardJ,
        string inputScriptPath,
        string sourceLabel,
        ValidationResult result)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pjassPath,
                Arguments = $"\"{commonJ}\" \"{blizzardJ}\" \"{inputScriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return;

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            var combined = (stdout + "\n" + stderr).Trim();
            if (proc.ExitCode == 0)
            {
                result.Diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Info",
                    Line = 1,
                    Source = "PJass 1.27b",
                    TriggerOrFile = sourceLabel,
                    Message = "Validación sintáctica PJass exitosa (0 errores contra common.j y blizzard.j de 1.27b)."
                });
            }
            else
            {
                int prevErrors = result.ErrorCount;
                if (!string.IsNullOrWhiteSpace(combined))
                    ParseCompilerOutput(combined, "PJass", sourceLabel, result);
                if (result.ErrorCount == prevErrors)
                {
                    result.ErrorCount++;
                    result.Diagnostics.Add(new CompilerDiagnostic
                    {
                        Severity = "Error",
                        Line = 1,
                        Source = "PJass",
                        TriggerOrFile = sourceLabel,
                        Message = string.IsNullOrWhiteSpace(combined)
                            ? $"pjass.exe falló con código de salida {proc.ExitCode}."
                            : combined
                    });
                }
            }
        }
        catch (Exception ex)
        {
            result.WarningCount++;
            result.Diagnostics.Add(new CompilerDiagnostic
            {
                Severity = "Warning",
                Line = 1,
                Source = "PJass",
                TriggerOrFile = sourceLabel,
                Message = $"Error ejecutando pjass.exe: {ex.Message}"
            });
        }
    }

    private static void ParseCompilerOutput(string output, string toolName, string sourceLabel, ValidationResult result)
    {
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int parsedSpecificErrors = 0;
        var fallbackErrorLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Match patterns like "war3map_input.j:142: syntax error..." or "Line 142: ..."
            var m1 = Regex.Match(trimmed, @":(\d+):\s*(.+)");
            var m2 = Regex.Match(trimmed, @"[Ll]ine\s+(\d+)\s*:\s*(.+)");

            if (m1.Success && int.TryParse(m1.Groups[1].Value, out var ln1))
            {
                parsedSpecificErrors++;
                result.ErrorCount++;
                result.Diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Error",
                    Line = ln1,
                    Source = toolName,
                    TriggerOrFile = sourceLabel,
                    Message = m1.Groups[2].Value.Trim()
                });
            }
            else if (m2.Success && int.TryParse(m2.Groups[1].Value, out var ln2))
            {
                parsedSpecificErrors++;
                result.ErrorCount++;
                result.Diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Error",
                    Line = ln2,
                    Source = toolName,
                    TriggerOrFile = sourceLabel,
                    Message = m2.Groups[2].Value.Trim()
                });
            }
            else if (trimmed.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                // Skip pjass/jasshelper summary footer lines ("... failed with N error", "Parse failed: N error total")
                // when specific line diagnostics were already captured
                bool isSummaryLine = Regex.IsMatch(trimmed, @"failed with \d+ error|^Parse failed:\s*\d+\s*error", RegexOptions.IgnoreCase);
                if (!isSummaryLine)
                    fallbackErrorLines.Add(trimmed);
            }
        }

        if (parsedSpecificErrors == 0)
        {
            foreach (var fallback in fallbackErrorLines)
            {
                result.ErrorCount++;
                result.Diagnostics.Add(new CompilerDiagnostic
                {
                    Severity = "Error",
                    Line = 1,
                    Source = toolName,
                    TriggerOrFile = sourceLabel,
                    Message = fallback
                });
            }
        }
    }

    public string LaunchMapInWarcraft3(string mapPath, WorkspaceSettings settings)
    {
        if (!File.Exists(settings.War3ExePath))
            throw new FileNotFoundException($"No se encontró war3.exe en: {settings.War3ExePath}");

        var ext = Path.GetExtension(mapPath);
        if (string.IsNullOrEmpty(ext))
            ext = ".w3x";

        // Cerrar instancias previas colgadas de war3.exe para evitar bloqueos del archivo de prueba
        try
        {
            var existingWar3 = Process.GetProcessesByName("war3");
            foreach (var p in existingWar3)
            {
                try { p.Kill(); p.WaitForExit(1000); }
                catch (Exception ex) { AppLogger.Warn("JassCompilerService.LaunchMapInWarcraft3", $"Could not kill existing war3.exe (PID {p.Id}).", ex); }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("JassCompilerService.LaunchMapInWarcraft3", "Could not enumerate existing war3 processes.", ex);
        }

        // Copy saved map into <Warcraft3Dir>\Maps\Test\WorldEditTestMap.w3x (official World Editor behavior)
        // so war3.exe -loadfile loads it reliably regardless of external folder paths or long filenames.
        var testMapDir = Path.Combine(settings.Warcraft3Dir, "Maps", "Test");
        Directory.CreateDirectory(testMapDir);
        var testFileName = $"WorldEditTestMap{ext}";
        var destTestPath = Path.Combine(testMapDir, testFileName);
        File.Copy(mapPath, destTestPath, overwrite: true);

        var relLoadPath = Path.Combine("Maps", "Test", testFileName);
        var psi = new ProcessStartInfo
        {
            FileName = settings.War3ExePath,
            Arguments = $"-loadfile \"{relLoadPath}\" {settings.TestCommandArgs}".Trim(),
            WorkingDirectory = settings.Warcraft3Dir,
            UseShellExecute = true
        };
        Process.Start(psi);
        return destTestPath;
    }
}

