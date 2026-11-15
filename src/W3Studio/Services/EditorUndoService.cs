using System;
using System.Collections.Generic;
using W3Studio.Models;

namespace W3Studio.Services;

public interface IUndoableEdit
{
    string Description { get; }
    void Undo();
    void Redo();
}

/// <summary>
/// Callback-based undo step for entity, region, and camera modifications.
/// </summary>
public sealed class DelegateUndoEdit : IUndoableEdit
{
    private readonly Action _undo;
    private readonly Action _redo;

    public string Description { get; }

    public DelegateUndoEdit(string description, Action undo, Action redo)
    {
        Description = description;
        _undo = undo;
        _redo = redo;
    }

    public void Undo() => _undo();
    public void Redo() => _redo();
}

/// <summary>
/// Stores sparse 7-byte war3map.w3e corner deltas for a single terrain brush stroke.
/// </summary>
public sealed class TerrainStrokeUndoEdit : IUndoableEdit
{
    private readonly MapProject _project;
    private readonly List<(int CornerIndex, byte[] Before7)> _before;
    private readonly List<(int CornerIndex, byte[] After7)> _after;
    private readonly Action<TerrainDabResult> _applyVisualPatch;

    public string Description { get; }

    private TerrainStrokeUndoEdit(
        string description,
        MapProject project,
        List<(int CornerIndex, byte[] Before7)> before,
        List<(int CornerIndex, byte[] After7)> after,
        Action<TerrainDabResult> applyVisualPatch)
    {
        Description = description;
        _project = project;
        _before = before;
        _after = after;
        _applyVisualPatch = applyVisualPatch;
    }

    /// <summary>
    /// Compares <paramref name="beforeW3eSnapshot"/> against the current <see cref="W3TerrainData.RawW3eBytes"/>
    /// and constructs an undo entry containing only modified corners, or null if nothing changed.
    /// </summary>
    public static TerrainStrokeUndoEdit? TryCreateFromSnapshots(
        string description,
        MapProject project,
        byte[] beforeW3eSnapshot,
        Action<TerrainDabResult> applyVisualPatch)
    {
        var afterRaw = project.Terrain.RawW3eBytes;
        if (afterRaw == null || beforeW3eSnapshot.Length != afterRaw.Length)
            return null;

        int headerLen = TerrainBrushService.GetW3eCornerDataOffset(afterRaw);
        int cornerCount = project.Terrain.GridWidth * project.Terrain.GridHeight;
        if (headerLen < 0 || headerLen + cornerCount * 7 > afterRaw.Length)
            return null;

        var beforeList = new List<(int, byte[])>();
        var afterList = new List<(int, byte[])>();

        for (int i = 0; i < cornerCount; i++)
        {
            int off = headerLen + i * 7;
            bool diff = false;
            for (int k = 0; k < 7; k++)
            {
                if (beforeW3eSnapshot[off + k] != afterRaw[off + k])
                {
                    diff = true;
                    break;
                }
            }

            if (diff)
            {
                var b = new byte[7];
                var a = new byte[7];
                Buffer.BlockCopy(beforeW3eSnapshot, off, b, 0, 7);
                Buffer.BlockCopy(afterRaw, off, a, 0, 7);
                beforeList.Add((i, b));
                afterList.Add((i, a));
            }
        }

        if (beforeList.Count == 0)
            return null;

        return new TerrainStrokeUndoEdit(description, project, beforeList, afterList, applyVisualPatch);
    }

    public void Undo()
    {
        var dab = TerrainBrushService.ApplyCornerBytePatches(_project, _before);
        if (dab.Modified)
            _applyVisualPatch(dab);
    }

    public void Redo()
    {
        var dab = TerrainBrushService.ApplyCornerBytePatches(_project, _after);
        if (dab.Modified)
            _applyVisualPatch(dab);
    }
}

/// <summary>
/// Bounded Undo/Redo stack for 3D World Editor operations (Ctrl+Z / Ctrl+Y).
/// </summary>
public sealed class EditorUndoService
{
    private const int MaxHistoryDepth = 100;
    private readonly List<IUndoableEdit> _undoStack = new();
    private readonly List<IUndoableEdit> _redoStack = new();

    public event Action? HistoryChanged;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public string? NextUndoDescription => _undoStack.Count > 0 ? _undoStack[^1].Description : null;
    public string? NextRedoDescription => _redoStack.Count > 0 ? _redoStack[^1].Description : null;

    public void Record(IUndoableEdit edit)
    {
        _undoStack.Add(edit);
        if (_undoStack.Count > MaxHistoryDepth)
            _undoStack.RemoveAt(0);
        _redoStack.Clear();
        HistoryChanged?.Invoke();
    }

    public string? Undo()
    {
        if (_undoStack.Count == 0)
            return null;

        var edit = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        edit.Undo();
        _redoStack.Add(edit);
        HistoryChanged?.Invoke();
        return edit.Description;
    }

    public string? Redo()
    {
        if (_redoStack.Count == 0)
            return null;

        var edit = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        edit.Redo();
        _undoStack.Add(edit);
        HistoryChanged?.Invoke();
        return edit.Description;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        HistoryChanged?.Invoke();
    }
}

