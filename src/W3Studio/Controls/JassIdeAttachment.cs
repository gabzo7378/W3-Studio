using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio.Controls;

public sealed class JassIdeAttachment : IDisposable
{
    private readonly TextEditor _editor;
    private readonly FoldingManager _foldingManager;
    private readonly DiagnosticSquiggleRenderer _squiggleRenderer;
    private readonly ToolTip _hoverToolTip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };

    private MapProject? _project;
    private WorkspaceSettings? _settings;
    private CompletionWindow? _completionWindow;
    private OverloadInsightWindow? _insightWindow;
    private bool _disposed;

    public JassIdeAttachment(TextEditor editor, MapProject? project = null, WorkspaceSettings? settings = null)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _project = project;
        _settings = settings;

        _foldingManager = FoldingManager.Install(_editor.TextArea);
        _squiggleRenderer = new DiagnosticSquiggleRenderer(_editor.Document);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_squiggleRenderer);

        // Preload SDK symbols asynchronously on background thread so first keypress is instant
        System.Threading.Tasks.Task.Run(() => JassKnowledgeService.GetSdkSymbols(_settings));

        _editor.TextArea.TextEntering += TextArea_TextEntering;
        _editor.TextArea.TextEntered += TextArea_TextEntered;
        _editor.TextArea.PreviewKeyDown += TextArea_PreviewKeyDown;
        _editor.MouseHover += TextArea_MouseHover;
        _editor.MouseHoverStopped += TextArea_MouseHoverStopped;
        _editor.TextChanged += Editor_TextChanged;
    }

    public void SetContext(MapProject? project, WorkspaceSettings? settings)
    {
        _project = project;
        _settings = settings;
    }

    public void RefreshFolding()
    {
        if (_disposed || _editor.Document == null) return;
        var foldings = JassFoldingStrategy.CreateFoldings(_editor.Document);
        _foldingManager.UpdateFoldings(foldings, -1);
    }

    public void UpdateDiagnostics(IEnumerable<CompilerDiagnostic> diagnostics)
    {
        if (_disposed) return;
        _squiggleRenderer.SetDiagnostics(diagnostics);
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        _editor.TextArea.TextView.Redraw();
    }

    public void FormatDocument()
    {
        if (_editor.IsReadOnly || _editor.Document == null) return;
        var caretLine = _editor.TextArea.Caret.Line;
        var formatted = JassKnowledgeService.FormatJassCode(_editor.Text);
        if (string.Equals(formatted, _editor.Text, StringComparison.Ordinal))
            return;

        _editor.Document.BeginUpdate();
        try
        {
            _editor.Document.Replace(0, _editor.Document.TextLength, formatted);
        }
        finally
        {
            _editor.Document.EndUpdate();
        }

        if (caretLine >= 1 && caretLine <= _editor.Document.LineCount)
        {
            var line = _editor.Document.GetLineByNumber(caretLine);
            _editor.TextArea.Caret.Offset = line.Offset;
            _editor.ScrollToLine(caretLine);
        }
        RefreshFolding();
    }

    public void ToggleLineComments()
    {
        if (_editor.IsReadOnly || _editor.Document == null) return;

        var doc = _editor.Document;
        var selStart = _editor.SelectionStart;
        var selEnd = selStart + _editor.SelectionLength;

        int startLineNum = doc.GetLineByOffset(selStart).LineNumber;
        int endLineNum = doc.GetLineByOffset(Math.Max(selStart, selEnd > selStart ? selEnd - 1 : selEnd)).LineNumber;

        var lines = new List<DocumentLine>();
        for (int i = startLineNum; i <= endLineNum; i++)
            lines.Add(doc.GetLineByNumber(i));

        bool allCommented = lines
            .Select(l => doc.GetText(l.Offset, l.Length))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .All(t => t.TrimStart().StartsWith("//", StringComparison.Ordinal));

        doc.BeginUpdate();
        try
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var l = lines[i];
                var text = doc.GetText(l.Offset, l.Length);
                if (allCommented)
                {
                    var slashIdx = text.IndexOf("//", StringComparison.Ordinal);
                    if (slashIdx >= 0)
                    {
                        int removeLen = (slashIdx + 2 < text.Length && text[slashIdx + 2] == ' ') ? 3 : 2;
                        doc.Remove(l.Offset + slashIdx, removeLen);
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        doc.Insert(l.Offset, "// ");
                    }
                    else
                    {
                        int indent = 0;
                        while (indent < text.Length && char.IsWhiteSpace(text[indent])) indent++;
                        doc.Insert(l.Offset + indent, "// ");
                    }
                }
            }
        }
        finally
        {
            doc.EndUpdate();
        }
    }

    public void ShowGoToLineDialog()
    {
        var owner = Window.GetWindow(_editor);
        var dlg = new Window
        {
            Title = "Ir a Línea",
            Width = 300,
            Height = 135,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner,
            Background = new SolidColorBrush(Color.FromRgb(245, 245, 245))
        };

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Número de línea (1 - {_editor.LineCount}):",
            Margin = new Thickness(0, 0, 0, 6)
        });

        var txt = new TextBox
        {
            Text = _editor.TextArea.Caret.Line.ToString(),
            Padding = new Thickness(4, 2, 4, 2)
        };
        txt.SelectAll();
        panel.Children.Add(txt);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var btnOk = new Button { Content = "Ir", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var btnCancel = new Button { Content = "Cancelar", Width = 70, IsCancel = true };

        btnOk.Click += (_, _) =>
        {
            if (int.TryParse(txt.Text.Trim(), out int target) && target >= 1 && target <= _editor.LineCount)
            {
                var line = _editor.Document.GetLineByNumber(target);
                _editor.Select(line.Offset, 0);
                _editor.ScrollToLine(target);
                _editor.Focus();
                dlg.DialogResult = true;
            }
            else
            {
                MessageBox.Show(dlg, $"Ingrese un número entre 1 y {_editor.LineCount}.", "Línea inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        btnPanel.Children.Add(btnOk);
        btnPanel.Children.Add(btnCancel);
        panel.Children.Add(btnPanel);
        dlg.Content = panel;
        txt.Focus();
        dlg.ShowDialog();
    }

    public void TriggerCompletion(bool explicitTrigger)
    {
        if (_editor.IsReadOnly || _editor.Document == null)
            return;

        int caretOffset = _editor.CaretOffset;
        if (IsInsideStringOrComment(caretOffset))
            return;

        string prefix = GetCurrentIdentifierPrefix(caretOffset, out int startOffset);
        if (!explicitTrigger && prefix.Length < 2)
            return;

        var candidates = CollectCompletionSymbols(caretOffset);
        var filtered = string.IsNullOrEmpty(prefix)
            ? candidates.Take(120).ToList()
            : candidates
                .Where(s => s.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ThenBy(s => s.Kind == "Local" ? 0 : s.Kind == "Global" ? 1 : s.Kind == "Snippet" ? 2 : 3)
                .ThenBy(s => s.Name.Length)
                .Take(80)
                .ToList();

        if (filtered.Count == 0)
        {
            _completionWindow?.Close();
            return;
        }

        _completionWindow?.Close();
        _completionWindow = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = startOffset,
            Width = 360,
            MaxHeight = 260,
            CloseWhenCaretAtBeginning = false
        };

        var data = _completionWindow.CompletionList.CompletionData;
        foreach (var sym in filtered)
            data.Add(new JassCompletionData(sym));

        if (!string.IsNullOrEmpty(prefix))
            _completionWindow.CompletionList.SelectItem(prefix);

        _completionWindow.Closed += (_, _) => _completionWindow = null;
        _completionWindow.Show();
    }

    public void TriggerParameterInsight()
    {
        if (_editor.Document == null) return;
        int caretOffset = _editor.CaretOffset;
        var line = _editor.Document.GetLineByOffset(caretOffset);
        var before = _editor.Document.GetText(line.Offset, caretOffset - line.Offset);

        var match = Regex.Match(before, @"([A-Za-z_][A-Za-z0-9_]*)\s*\([^()]*$");
        if (!match.Success) return;

        var funcName = match.Groups[1].Value;
        var sym = JassKnowledgeService.FindSymbol(funcName, _settings, _project, _editor.Text);
        if (sym == null || string.IsNullOrWhiteSpace(sym.Signature))
            return;

        _insightWindow?.Close();
        _insightWindow = new OverloadInsightWindow(_editor.TextArea)
        {
            Provider = new JassOverloadProvider(sym)
        };
        _insightWindow.Closed += (_, _) => _insightWindow = null;
        _insightWindow.Show();
    }

    private List<JassSymbol> CollectCompletionSymbols(int caretOffset)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<JassSymbol>();

        foreach (var loc in JassKnowledgeService.ExtractLocalsInScope(_editor.Text, caretOffset))
        {
            if (seen.Add(loc.Name)) list.Add(loc);
        }

        foreach (var cur in JassKnowledgeService.ExtractScriptSymbols(_editor.Text, "Script actual"))
        {
            if (seen.Add(cur.Name)) list.Add(cur);
        }

        foreach (var projSym in JassKnowledgeService.GetProjectSymbols(_project))
        {
            if (seen.Add(projSym.Name)) list.Add(projSym);
        }

        foreach (var sdkSym in JassKnowledgeService.GetSdkSymbols(_settings))
        {
            if (seen.Add(sdkSym.Name)) list.Add(sdkSym);
        }

        return list;
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        RefreshFolding();
    }

    private void TextArea_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;

        if (mods == ModifierKeys.Control && e.Key == Key.Space)
        {
            e.Handled = true;
            TriggerCompletion(explicitTrigger: true);
            return;
        }

        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Space)
        {
            e.Handled = true;
            TriggerParameterInsight();
            return;
        }

        if (mods == ModifierKeys.Control && (e.Key == Key.OemQuestion || e.Key == Key.Divide || e.Key == Key.Oem2))
        {
            e.Handled = true;
            ToggleLineComments();
            return;
        }

        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.F)
        {
            e.Handled = true;
            FormatDocument();
            return;
        }

        if (mods == ModifierKeys.Control && e.Key == Key.G)
        {
            e.Handled = true;
            ShowGoToLineDialog();
        }
    }

    private void TextArea_TextEntering(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length > 0 && _completionWindow != null)
        {
            char c = e.Text[0];
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                if (c == '(' || c == '\t')
                    _completionWindow.CompletionList.RequestInsertion(e);
                else if (c == ' ' || c == ')' || c == ',')
                    _completionWindow.Close();
            }
        }
    }

    private void TextArea_TextEntered(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
            return;

        char ch = e.Text[0];
        if (ch == '(')
        {
            TriggerParameterInsight();
            return;
        }
        if (ch == ')')
        {
            _insightWindow?.Close();
            return;
        }

        if (char.IsLetter(ch) || ch == '_')
        {
            if (_completionWindow == null)
                TriggerCompletion(explicitTrigger: false);
        }
    }

    private void TextArea_MouseHover(object sender, MouseEventArgs e)
    {
        var pos = _editor.GetPositionFromPoint(e.GetPosition(_editor));
        if (!pos.HasValue) return;

        int line = pos.Value.Line;
        var diagMsg = _squiggleRenderer.GetMessageForLine(line);
        if (!string.IsNullOrEmpty(diagMsg))
        {
            _hoverToolTip.Content = diagMsg;
            _hoverToolTip.IsOpen = true;
            return;
        }

        var docLine = _editor.Document.GetLineByNumber(line);
        int colOffset = Math.Clamp(pos.Value.Column - 1, 0, docLine.Length);
        int offset = docLine.Offset + colOffset;
        string word = GetIdentifierAtOffset(offset);
        if (!string.IsNullOrEmpty(word))
        {
            var sym = JassKnowledgeService.FindSymbol(word, _settings, _project, _editor.Text);
            if (sym != null)
            {
                _hoverToolTip.Content = $"{sym.Signature}\n[{sym.DisplayKind}] {sym.Description}";
                _hoverToolTip.IsOpen = true;
            }
        }
    }

    private void TextArea_MouseHoverStopped(object sender, MouseEventArgs e)
    {
        _hoverToolTip.IsOpen = false;
    }

    private string GetCurrentIdentifierPrefix(int caretOffset, out int startOffset)
    {
        startOffset = caretOffset;
        var doc = _editor.Document;
        while (startOffset > 0)
        {
            char c = doc.GetCharAt(startOffset - 1);
            if (char.IsLetterOrDigit(c) || c == '_')
                startOffset--;
            else
                break;
        }
        return startOffset < caretOffset ? doc.GetText(startOffset, caretOffset - startOffset) : string.Empty;
    }

    private string GetIdentifierAtOffset(int offset)
    {
        var doc = _editor.Document;
        if (offset < 0 || offset >= doc.TextLength)
            return string.Empty;

        int start = offset;
        while (start > 0 && (char.IsLetterOrDigit(doc.GetCharAt(start - 1)) || doc.GetCharAt(start - 1) == '_'))
            start--;

        int end = offset;
        while (end < doc.TextLength && (char.IsLetterOrDigit(doc.GetCharAt(end)) || doc.GetCharAt(end) == '_'))
            end++;

        return end > start ? doc.GetText(start, end - start) : string.Empty;
    }

    private bool IsInsideStringOrComment(int offset)
    {
        var doc = _editor.Document;
        var line = doc.GetLineByOffset(offset);
        var before = doc.GetText(line.Offset, offset - line.Offset);
        bool inStr = false;
        for (int i = 0; i < before.Length; i++)
        {
            char c = before[i];
            if (c == '"' && (i == 0 || before[i - 1] != '\\'))
                inStr = !inStr;
            else if (!inStr && c == '/' && i + 1 < before.Length && before[i + 1] == '/')
                return true;
        }
        return inStr;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _completionWindow?.Close();
        _insightWindow?.Close();
        _editor.TextArea.TextEntering -= TextArea_TextEntering;
        _editor.TextArea.TextEntered -= TextArea_TextEntered;
        _editor.TextArea.PreviewKeyDown -= TextArea_PreviewKeyDown;
        _editor.MouseHover -= TextArea_MouseHover;
        _editor.MouseHoverStopped -= TextArea_MouseHoverStopped;
        _editor.TextChanged -= Editor_TextChanged;
        _editor.TextArea.TextView.BackgroundRenderers.Remove(_squiggleRenderer);
        FoldingManager.Uninstall(_foldingManager);
    }

    private sealed class JassCompletionData : ICompletionData
    {
        private readonly JassSymbol _symbol;

        public JassCompletionData(JassSymbol symbol)
        {
            _symbol = symbol;
        }

        public ImageSource? Image => null;
        public string Text => _symbol.Name;

        public object Content
        {
            get
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                var badgeColor = _symbol.Kind switch
                {
                    "Native" => "#4EC9B0",
                    "BJ" => "#569CD6",
                    "Constant" => "#4FC1FF",
                    "Global" or "Trigger" or "Region" or "Unit" => "#DCDCAA",
                    "Local" => "#9CDCFE",
                    "Snippet" => "#C586C0",
                    _ => "#CCCCCC"
                };
                sp.Children.Add(new TextBlock
                {
                    Text = $"[{_symbol.Kind}] ",
                    Foreground = (Brush)new BrushConverter().ConvertFromString(badgeColor)!,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                });
                sp.Children.Add(new TextBlock
                {
                    Text = _symbol.Name,
                    FontFamily = new FontFamily("Consolas"),
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                return sp;
            }
        }

        public object Description => string.IsNullOrWhiteSpace(_symbol.Signature)
            ? _symbol.Description
            : $"{_symbol.Signature}\n{_symbol.Description}";

        public double Priority => _symbol.Kind switch
        {
            "Local" => 4.0,
            "Global" => 3.5,
            "Snippet" => 3.0,
            "Native" => 2.5,
            "BJ" => 2.0,
            _ => 1.0
        };

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            var insertText = !string.IsNullOrEmpty(_symbol.InsertTemplate)
                ? _symbol.InsertTemplate
                : _symbol.Name;
            textArea.Document.Replace(completionSegment, insertText);
        }
    }

    private sealed class JassOverloadProvider : IOverloadProvider
    {
        private readonly JassSymbol _symbol;
        private int _selectedIndex;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public JassOverloadProvider(JassSymbol symbol)
        {
            _symbol = symbol;
        }

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                _selectedIndex = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedIndex)));
            }
        }

        public int Count => 1;
        public string? CurrentIndexText => null;
        public object CurrentHeader => $"{_symbol.Name}({_symbol.Parameters}) -> {_symbol.ReturnType}";
        public object CurrentContent => _symbol.Description;
    }

    private sealed class DiagnosticSquiggleRenderer : IBackgroundRenderer
    {
        private readonly TextDocument _document;
        private readonly List<(int Line, string Severity, string Message)> _entries = new();

        private static readonly Pen ErrorPen = CreateSquigglePen(Color.FromRgb(244, 71, 71));
        private static readonly Pen WarningPen = CreateSquigglePen(Color.FromRgb(204, 167, 0));
        private static readonly Pen InfoPen = CreateSquigglePen(Color.FromRgb(55, 148, 255));

        private static readonly Brush ErrorLineTint = new SolidColorBrush(Color.FromArgb(26, 244, 71, 71));
        private static readonly Brush WarningLineTint = new SolidColorBrush(Color.FromArgb(20, 204, 167, 0));

        public DiagnosticSquiggleRenderer(TextDocument document)
        {
            _document = document;
        }

        private static Pen CreateSquigglePen(Color color)
        {
            var pen = new Pen(new SolidColorBrush(color), 1.4);
            pen.Freeze();
            return pen;
        }

        public KnownLayer Layer => KnownLayer.Selection;

        public void SetDiagnostics(IEnumerable<CompilerDiagnostic> diagnostics)
        {
            _entries.Clear();
            foreach (var d in diagnostics)
            {
                if (d.Line > 0)
                    _entries.Add((d.Line, d.Severity, $"{d.SeverityIcon} [{d.Source}] {d.Message}"));
            }
        }

        public string? GetMessageForLine(int line)
        {
            var msgs = _entries.Where(e => e.Line == line).Select(e => e.Message).ToList();
            return msgs.Count > 0 ? string.Join("\n", msgs) : null;
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_entries.Count == 0 || !textView.VisualLinesValid)
                return;

            foreach (var entry in _entries)
            {
                if (entry.Line < 1 || entry.Line > _document.LineCount)
                    continue;

                var docLine = _document.GetLineByNumber(entry.Line);
                if (docLine.Length == 0)
                    continue;

                var text = _document.GetText(docLine.Offset, docLine.Length);
                int trimStart = 0;
                while (trimStart < text.Length && char.IsWhiteSpace(text[trimStart])) trimStart++;
                int trimEnd = text.Length;
                while (trimEnd > trimStart && char.IsWhiteSpace(text[trimEnd - 1])) trimEnd--;
                if (trimEnd <= trimStart)
                    continue;

                var segment = new TextSegment { StartOffset = docLine.Offset + trimStart, EndOffset = docLine.Offset + trimEnd };
                var pen = entry.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)
                    ? ErrorPen
                    : entry.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase)
                        ? WarningPen
                        : InfoPen;
                var tint = entry.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)
                    ? ErrorLineTint
                    : entry.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase)
                        ? WarningLineTint
                        : null;

                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    if (tint != null)
                        drawingContext.DrawRectangle(tint, null, rect);

                    DrawWaveUnderline(drawingContext, pen, rect.BottomLeft, rect.BottomRight);
                }
            }
        }

        private static void DrawWaveUnderline(DrawingContext dc, Pen pen, Point start, Point end)
        {
            const double step = 4.0;
            const double amp = 2.0;
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(start, false, false);
                double x = start.X;
                bool up = true;
                while (x < end.X)
                {
                    double nextX = Math.Min(x + step, end.X);
                    double y = start.Y + (up ? -amp : 0.0);
                    ctx.LineTo(new Point(nextX, y), true, false);
                    x = nextX;
                    up = !up;
                }
            }
            geom.Freeze();
            dc.DrawGeometry(null, pen, geom);
        }
    }

    private static class JassFoldingStrategy
    {
        public static List<NewFolding> CreateFoldings(TextDocument document)
        {
            var foldings = new List<NewFolding>();
            var stack = new Stack<(string Tag, int Offset, string Label)>();
            int commentBlockStart = -1;

            for (int i = 1; i <= document.LineCount; i++)
            {
                var line = document.GetLineByNumber(i);
                var raw = document.GetText(line.Offset, line.Length);
                var trimmed = raw.Trim();

                if (commentBlockStart < 0 && trimmed.StartsWith("/*", StringComparison.Ordinal) && !trimmed.Contains("*/", StringComparison.Ordinal))
                {
                    commentBlockStart = line.Offset;
                    continue;
                }
                if (commentBlockStart >= 0)
                {
                    if (trimmed.Contains("*/", StringComparison.Ordinal))
                    {
                        foldings.Add(new NewFolding(commentBlockStart, line.EndOffset) { Name = "/* ... */" });
                        commentBlockStart = -1;
                    }
                    continue;
                }

                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;

                if (TryMatchOpener(trimmed, out var openTag, out var label))
                {
                    stack.Push((openTag, line.Offset, label));
                }
                else if (TryMatchCloser(trimmed, out var closeTag))
                {
                    while (stack.Count > 0)
                    {
                        var top = stack.Pop();
                        if (string.Equals(top.Tag, closeTag, StringComparison.OrdinalIgnoreCase))
                        {
                            if (line.EndOffset > top.Offset)
                                foldings.Add(new NewFolding(top.Offset, line.EndOffset) { Name = top.Label });
                            break;
                        }
                    }
                }
            }

            foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
            return foldings;
        }

        private static bool TryMatchOpener(string trimmed, out string tag, out string label)
        {
            if (Regex.IsMatch(trimmed, @"^(?:public\s+|private\s+|static\s+|constant\s+)*function\s+([A-Za-z_][A-Za-z0-9_]*)\b"))
            {
                tag = "function";
                label = trimmed;
                return true;
            }
            if (Regex.IsMatch(trimmed, @"^(?:public\s+|private\s+|static\s+)*method\s+([A-Za-z_][A-Za-z0-9_]*)\b"))
            {
                tag = "method";
                label = trimmed;
                return true;
            }
            if (Regex.IsMatch(trimmed, @"^(?:public\s+|private\s+)?library\s+"))
            {
                tag = "library";
                label = trimmed;
                return true;
            }
            if (Regex.IsMatch(trimmed, @"^(?:public\s+|private\s+)?scope\s+"))
            {
                tag = "scope";
                label = trimmed;
                return true;
            }
            if (Regex.IsMatch(trimmed, @"^(?:public\s+|private\s+)?struct\s+"))
            {
                tag = "struct";
                label = trimmed;
                return true;
            }
            if (trimmed.Equals("globals", StringComparison.OrdinalIgnoreCase))
            {
                tag = "globals";
                label = "globals ...";
                return true;
            }
            if (Regex.IsMatch(trimmed, @"^if\b.*\bthen$"))
            {
                tag = "if";
                label = trimmed;
                return true;
            }
            if (trimmed.Equals("loop", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("loop ", StringComparison.OrdinalIgnoreCase))
            {
                tag = "loop";
                label = "loop ...";
                return true;
            }

            tag = string.Empty;
            label = string.Empty;
            return false;
        }

        private static bool TryMatchCloser(string trimmed, out string tag)
        {
            if (trimmed.StartsWith("endfunction", StringComparison.OrdinalIgnoreCase)) { tag = "function"; return true; }
            if (trimmed.StartsWith("endmethod", StringComparison.OrdinalIgnoreCase)) { tag = "method"; return true; }
            if (trimmed.StartsWith("endlibrary", StringComparison.OrdinalIgnoreCase)) { tag = "library"; return true; }
            if (trimmed.StartsWith("endscope", StringComparison.OrdinalIgnoreCase)) { tag = "scope"; return true; }
            if (trimmed.StartsWith("endstruct", StringComparison.OrdinalIgnoreCase)) { tag = "struct"; return true; }
            if (trimmed.StartsWith("endglobals", StringComparison.OrdinalIgnoreCase)) { tag = "globals"; return true; }
            if (trimmed.StartsWith("endif", StringComparison.OrdinalIgnoreCase)) { tag = "if"; return true; }
            if (trimmed.StartsWith("endloop", StringComparison.OrdinalIgnoreCase)) { tag = "loop"; return true; }
            tag = string.Empty;
            return false;
        }
    }
}
