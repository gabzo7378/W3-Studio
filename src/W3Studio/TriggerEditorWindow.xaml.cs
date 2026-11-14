using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using W3Studio.Controls;
using W3Studio.Models;
using W3Studio.Services;

namespace W3Studio;

public partial class TriggerEditorWindow : Window
{
    private readonly JassCompilerService _compilerService;
    private readonly WorkspaceSettings _settings;
    private readonly JassIdeAttachment _ideAttachment;
    private readonly DispatcherTimer _liveAnalyzeTimer;

    private MapProject? _project;
    private TriggerNode? _selectedNode;
    private bool _isUpdatingUi;
    private W3GlobalVariable? _editingVariable;

    private readonly ObservableCollection<CompilerDiagnostic> _diagnostics = new();
    private readonly ObservableCollection<W3GlobalVariable> _variablesView = new();

    public event Action? RequestOpenObjectEditor;
    public event Func<System.Threading.Tasks.Task<bool>>? RequestSaveAndCompile;

    public TriggerEditorWindow(
        JassCompilerService compilerService,
        WorkspaceSettings settings,
        MapProject? project,
        TriggerNode? initialNode = null)
    {
        InitializeComponent();

        _compilerService = compilerService;
        _settings = settings;

        CodeEditor.SyntaxHighlighting = VjassSyntaxHighlighter.GetDefinition();
        GuiPreviewEditor.SyntaxHighlighting = VjassSyntaxHighlighter.GetDefinition();
        GridDiagnostics.ItemsSource = _diagnostics;
        GridVariables.ItemsSource = _variablesView;

        _ideAttachment = new JassIdeAttachment(CodeEditor, project, settings);

        _liveAnalyzeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _liveAnalyzeTimer.Tick += LiveAnalyzeTimer_Tick;

        Closed += (_, _) =>
        {
            _liveAnalyzeTimer.Stop();
            _ideAttachment.Dispose();
        };

        LoadProject(project);

        if (initialNode != null)
            SelectTriggerNode(initialNode);
    }

    public void LoadProject(MapProject? project)
    {
        _project = project;
        _ideAttachment.SetContext(project, _settings);

        if (_project == null)
        {
            TreeTriggers.ItemsSource = null;
            _selectedNode = null;
            ClearDetails();
            RefreshVariablesGrid();
            RefreshMapRefsList();
            TxtStatusNodeInfo.Text = "No hay ningún mapa cargado.";
            return;
        }

        TreeTriggers.ItemsSource = _project.TriggerTree;
        TxtStatusNodeInfo.Text = $"Triggers: {_project.AllTriggerNodes.Count} elementos en '{_project.FileName}'";

        RefreshNativesList();
        RefreshVariablesGrid();
        RefreshMapRefsList();

        var first = _project.AllTriggerNodes.FirstOrDefault(n => n.Kind == TriggerNodeKind.GuiTrigger)
                 ?? _project.AllTriggerNodes.FirstOrDefault(n => n.Kind != TriggerNodeKind.Category)
                 ?? _project.TriggerTree.FirstOrDefault();
        if (first != null)
            SelectTriggerNode(first);
    }

    public void SelectTriggerNode(TriggerNode node)
    {
        _selectedNode = node;
        _isUpdatingUi = true;

        try
        {
            TxtTriggerName.Text = node.Name;
            TxtTriggerComment.Text = node.Comment ?? string.Empty;
            ChkEnabled.IsChecked = node.IsEnabled;
            ChkInitiallyOn.IsChecked = node.IsInitiallyOn;

            ImgTriggerIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(node.IconUri));

            if (node.Kind == TriggerNodeKind.GuiTrigger && !node.IsCustomText)
            {
                RbGuiMode.IsChecked = true;
                PanelGuiMode.Visibility = Visibility.Visible;
                PanelCodeMode.Visibility = Visibility.Collapsed;
                BtnToolbarConvert.IsEnabled = true;

                ListEvents.ItemsSource = node.Events;
                ListConditions.ItemsSource = node.Conditions;
                ListActions.ItemsSource = node.Actions;

                UpdateGuiCounts();
                RefreshGuiPreviewIfExpanded();
            }
            else
            {
                RbCodeMode.IsChecked = true;
                PanelGuiMode.Visibility = Visibility.Collapsed;
                PanelCodeMode.Visibility = Visibility.Visible;
                BtnToolbarConvert.IsEnabled = (node.Kind == TriggerNodeKind.GuiTrigger);

                TxtCodeModeTitle.Text = node.Name;
                TxtCodeModeMeta.Text = $"Categoría: {node.CategoryName} | Tipo: {node.Kind}";

                CodeEditor.Text = node.Code ?? string.Empty;
                BtnSyncFromGui.Visibility = (node.Kind == TriggerNodeKind.GuiTrigger) ? Visibility.Visible : Visibility.Collapsed;
                _ideAttachment.RefreshFolding();
                RunLiveLeakAnalysis();
            }

            TxtStatusNodeInfo.Text = $"Seleccionado: {node.Name} ({node.Kind})";
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void ClearDetails()
    {
        TxtTriggerName.Text = string.Empty;
        TxtTriggerComment.Text = string.Empty;
        ListEvents.ItemsSource = null;
        ListConditions.ItemsSource = null;
        ListActions.ItemsSource = null;
        CodeEditor.Text = string.Empty;
        GuiPreviewEditor.Text = string.Empty;
    }

    private void UpdateGuiCounts()
    {
        if (_selectedNode == null) return;
        TxtEventsCount.Text = $"({_selectedNode.Events.Count})";
        TxtConditionsCount.Text = $"({_selectedNode.Conditions.Count})";
        TxtActionsCount.Text = $"({_selectedNode.Actions.Count})";
    }

    private void RefreshGuiPreviewIfExpanded()
    {
        if (_selectedNode == null || ExpGuiJassPreview == null || !ExpGuiJassPreview.IsExpanded)
            return;

        if (_selectedNode.Kind == TriggerNodeKind.GuiTrigger)
        {
            var generated = _selectedNode.GenerateJassFromGui();
            var oldCode = _selectedNode.Code;
            _selectedNode.Code = generated;
            GuiPreviewEditor.Text = generated;
            JassKnowledgeService.SyncNodeCodeToProject(_project, _selectedNode, oldCode, generated);
        }
        else
        {
            GuiPreviewEditor.Text = _selectedNode.Code ?? string.Empty;
        }
    }

    private void SyncSelectedGuiNodeToScript()
    {
        if (_selectedNode == null || _selectedNode.Kind != TriggerNodeKind.GuiTrigger)
            return;

        var oldCode = _selectedNode.Code;
        var generated = _selectedNode.GenerateJassFromGui();
        _selectedNode.Code = generated;
        _selectedNode.IsModified = true;
        JassKnowledgeService.SyncNodeCodeToProject(_project, _selectedNode, oldCode, generated);
        RefreshGuiPreviewIfExpanded();
    }

    private void ExpGuiJassPreview_Expanded(object sender, RoutedEventArgs e)
    {
        RefreshGuiPreviewIfExpanded();
    }

    private void TreeTriggers_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TriggerNode node)
            SelectTriggerNode(node);
    }

    private void TxtTriggerName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingUi || _selectedNode == null) return;
        _selectedNode.Name = TxtTriggerName.Text.Trim();
        _selectedNode.IsModified = true;
        if (_selectedNode.Kind == TriggerNodeKind.GuiTrigger)
            SyncSelectedGuiNodeToScript();
    }

    private void TxtTriggerComment_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingUi || _selectedNode == null) return;
        _selectedNode.Comment = TxtTriggerComment.Text;
        _selectedNode.IsModified = true;
        if (_selectedNode.Kind == TriggerNodeKind.GuiTrigger)
            SyncSelectedGuiNodeToScript();
    }

    private void ChkEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi || _selectedNode == null) return;
        _selectedNode.IsEnabled = ChkEnabled.IsChecked == true;
        _selectedNode.IsModified = true;
        if (_selectedNode.Kind == TriggerNodeKind.GuiTrigger)
            SyncSelectedGuiNodeToScript();
    }

    private void ChkInitiallyOn_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi || _selectedNode == null) return;
        _selectedNode.IsInitiallyOn = ChkInitiallyOn.IsChecked == true;
        _selectedNode.IsModified = true;
        if (_selectedNode.Kind == TriggerNodeKind.GuiTrigger)
            SyncSelectedGuiNodeToScript();
    }

    private void GuiElementEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode == null) return;
        _selectedNode.IsModified = true;
        SyncSelectedGuiNodeToScript();
    }

    private void RbMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi || _selectedNode == null) return;

        if (RbGuiMode.IsChecked == true)
        {
            PanelGuiMode.Visibility = Visibility.Visible;
            PanelCodeMode.Visibility = Visibility.Collapsed;
            RefreshGuiPreviewIfExpanded();
        }
        else
        {
            PanelGuiMode.Visibility = Visibility.Collapsed;
            PanelCodeMode.Visibility = Visibility.Visible;

            if (string.IsNullOrWhiteSpace(_selectedNode.Code) && _selectedNode.Kind == TriggerNodeKind.GuiTrigger)
            {
                _isUpdatingUi = true;
                CodeEditor.Text = _selectedNode.GenerateJassFromGui();
                _isUpdatingUi = false;
                _selectedNode.Code = CodeEditor.Text;
            }
            else
            {
                _isUpdatingUi = true;
                CodeEditor.Text = _selectedNode.Code ?? string.Empty;
                _isUpdatingUi = false;
            }

            TxtCodeModeTitle.Text = _selectedNode.Name;
            TxtCodeModeMeta.Text = $"Categoría: {_selectedNode.CategoryName} | Tipo: {_selectedNode.Kind}";
            BtnSyncFromGui.Visibility = (_selectedNode.Kind == TriggerNodeKind.GuiTrigger) ? Visibility.Visible : Visibility.Collapsed;
            _ideAttachment.RefreshFolding();
            RunLiveLeakAnalysis();
        }
    }

    // ==================== EVENTOS GUI ====================

    private void BtnAddEvent_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode == null) return;
        var dlg = new EditTriggerEventDialog(null, _project) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _selectedNode.Events.Add(dlg.ResultElement);
            UpdateGuiCounts();
            SyncSelectedGuiNodeToScript();
            ListEvents.SelectedItem = dlg.ResultElement;
        }
    }

    private void BtnEditEvent_Click(object sender, RoutedEventArgs e) => EditSelectedEvent();
    private void ListEvents_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedEvent();

    private void EditSelectedEvent()
    {
        if (_selectedNode == null || ListEvents.SelectedItem is not GuiTriggerElement ev) return;
        var dlg = new EditTriggerEventDialog(ev, _project) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            var idx = _selectedNode.Events.IndexOf(ev);
            if (idx >= 0) _selectedNode.Events[idx] = dlg.ResultElement;
            UpdateGuiCounts();
            SyncSelectedGuiNodeToScript();
        }
    }

    private void BtnDeleteEvent_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode == null || ListEvents.SelectedItem is not GuiTriggerElement ev) return;
        _selectedNode.Events.Remove(ev);
        UpdateGuiCounts();
        SyncSelectedGuiNodeToScript();
    }

    // ==================== CONDICIONES GUI ====================

    private void BtnAddCondition_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode == null) return;
        var dlg = new EditTriggerConditionDialog(null, _project) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _selectedNode.Conditions.Add(dlg.ResultElement);
            UpdateGuiCounts();
            SyncSelectedGuiNodeToScript();
            ListConditions.SelectedItem = dlg.ResultElement;
        }
    }

    private void BtnEditCondition_Click(object sender, RoutedEventArgs e) => EditSelectedCondition();
    private void ListConditions_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedCondition();

    private void EditSelectedCondition()
    {
        if (_selectedNode == null || ListConditions.SelectedItem is not GuiTriggerElement cond) return;
        var dlg = new EditTriggerConditionDialog(cond, _project) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            var idx = _selectedNode.Conditions.IndexOf(cond);
            if (idx >= 0) _selectedNode.Conditions[idx] = dlg.ResultElement;
            UpdateGuiCounts();
            SyncSelectedGuiNodeToScript();
        }
    }

    private void BtnDeleteCondition_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode == null || ListConditions.SelectedItem is not GuiTriggerElement cond) return;
        _selectedNode.Conditions.Remove(cond);
        UpdateGuiCounts();
        SyncSelectedGuiNodeToScript();
    }

    // ==================== ACCIONES GUI ====================

    private void BtnAddAction_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode == null) return;
        var dlg = new EditTriggerActionDialog(null, _project) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _selectedNode.Actions.Add(dlg.ResultElement);
            UpdateGuiCounts();
            SyncSelectedGuiNodeToScript();
            ListActions.SelectedItem = dlg.ResultElement;
        }
    }

    private void BtnEditAction_Click(object sender, RoutedEventArgs e) => EditSelectedAction();
    private void ListActions_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedAction();

    private void EditSelectedAction()
    {
        if (_selectedNode == null || ListActions.SelectedItem is not GuiTriggerElement act) return;
        var dlg = new EditTriggerActionDialog(act, _project) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            var idx = _selectedNode.Actions.IndexOf(act);
            if (idx >= 0) _selectedNode.Actions[idx] = dlg.ResultElement;
            UpdateGuiCounts();
            SyncSelectedGuiNodeToScript();
        }
    }

    private void BtnDeleteAction_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode == null || ListActions.SelectedItem is not GuiTriggerElement act) return;
        _selectedNode.Actions.Remove(act);
        UpdateGuiCounts();
        SyncSelectedGuiNodeToScript();
    }

    private void BtnMoveActionUp_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode == null || ListActions.SelectedItem is not GuiTriggerElement act) return;
        var idx = _selectedNode.Actions.IndexOf(act);
        if (idx > 0)
        {
            _selectedNode.Actions.Move(idx, idx - 1);
            SyncSelectedGuiNodeToScript();
            ListActions.SelectedItem = act;
        }
    }

    private void BtnMoveActionDown_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode == null || ListActions.SelectedItem is not GuiTriggerElement act) return;
        var idx = _selectedNode.Actions.IndexOf(act);
        if (idx >= 0 && idx < _selectedNode.Actions.Count - 1)
        {
            _selectedNode.Actions.Move(idx, idx + 1);
            SyncSelectedGuiNodeToScript();
            ListActions.SelectedItem = act;
        }
    }

    // ==================== CONVERSIÓN Y CREACIÓN ====================

    private void BtnConvertToCustomText_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode == null)
        {
            MessageBox.Show(this, "Por favor seleccione un disparador para convertir.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedNode.Kind != TriggerNodeKind.GuiTrigger)
        {
            MessageBox.Show(this, "El elemento seleccionado ya es un script o bloque de texto personalizado.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var res = MessageBox.Show(
            this,
            $"¿Está seguro de que desea convertir el disparador '{_selectedNode.Name}' a texto personalizado (JASS / vJASS)?\n\n" +
            "Esta acción transformará los eventos, condiciones y acciones GUI en código JASS editable.",
            "Convertir Disparador a Texto Personalizado",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes) return;

        var oldCode = _selectedNode.Code;
        var generatedJass = _selectedNode.GenerateJassFromGui();
        _selectedNode.Code = generatedJass;
        _selectedNode.Kind = TriggerNodeKind.CustomTextTrigger;
        _selectedNode.IsCustomText = true;
        _selectedNode.IsModified = true;
        JassKnowledgeService.SyncNodeCodeToProject(_project, _selectedNode, oldCode, generatedJass);

        SelectTriggerNode(_selectedNode);
        MessageBox.Show(this, $"El disparador '{_selectedNode.Name}' se ha convertido con éxito a texto personalizado JASS.", "Conversión Exitosa", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnSyncFromGui_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode == null) return;
        var generated = _selectedNode.GenerateJassFromGui();
        CodeEditor.Text = generated;
        SyncSelectedGuiNodeToScript();
    }

    private void BtnNewGuiTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null)
        {
            MessageBox.Show(this, "Debe abrir un mapa para añadir disparadores.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var newIndex = _project.AllTriggerNodes.Count(n => n.Kind == TriggerNodeKind.GuiTrigger) + 1;
        var nodeName = $"DisparadorSinTitulo_{newIndex:D3}";

        var node = new TriggerNode
        {
            Name = nodeName,
            Kind = TriggerNodeKind.GuiTrigger,
            CategoryName = "General",
            IsEnabled = true,
            IsInitiallyOn = true,
            IsModified = true
        };

        node.Events.Add(new GuiTriggerElement
        {
            ElementType = GuiTriggerElementType.Event,
            Category = "Map",
            FunctionName = "MapInitialization",
            DisplayText = "Map initialization"
        });

        var action = new GuiTriggerElement
        {
            ElementType = GuiTriggerElementType.Action,
            Category = "Game",
            FunctionName = "DisplayTimedText",
            DisplayText = "Game - Display to (All players) the text: ¡Bienvenido al mapa!"
        };
        action.Parameters["Text"] = "¡Bienvenido al mapa!";
        action.Parameters["Duration"] = "10.0";
        node.Actions.Add(action);
        node.Code = node.GenerateJassFromGui();

        InsertTriggerIntoTree(node);
        SelectTriggerNode(node);
        RefreshMapRefsList();
    }

    private void BtnNewVjassTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null)
        {
            MessageBox.Show(this, "Debe abrir un mapa para añadir scripts vJASS.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var newIndex = _project.AllTriggerNodes.Count(n => n.Kind == TriggerNodeKind.LibraryOrScope) + 1;
        var nodeName = $"vJASS_Script_{newIndex:D2}";

        var initialCode =
            $"scope {nodeName} initializer Init\n\n" +
            "    private function Actions takes nothing returns nothing\n" +
            "        // Tu lógica vJASS (1.27b) aquí\n" +
            "    endfunction\n\n" +
            "    private function Init takes nothing returns nothing\n" +
            "        local trigger t = CreateTrigger()\n" +
            "        call TriggerRegisterTimerEvent(t, 1.00, false)\n" +
            "        call TriggerAddAction(t, function Actions)\n" +
            "        set t = null\n" +
            "    endfunction\n\n" +
            "endscope\n";

        var node = new TriggerNode
        {
            Name = nodeName,
            Kind = TriggerNodeKind.LibraryOrScope,
            CategoryName = "Scripts vJASS",
            Code = initialCode,
            IsEnabled = true,
            IsInitiallyOn = true,
            IsModified = true
        };

        var targetCategory = _project.TriggerTree.FirstOrDefault(t => t.Name.Contains("vJASS", StringComparison.OrdinalIgnoreCase));
        if (targetCategory != null)
            targetCategory.Children.Add(node);
        else
            InsertTriggerIntoTree(node);

        if (!_project.AllTriggerNodes.Contains(node))
            _project.AllTriggerNodes.Add(node);

        JassKnowledgeService.SyncNodeCodeToProject(_project, node, string.Empty, initialCode);
        SelectTriggerNode(node);
        RefreshMapRefsList();
    }

    private void BtnDuplicateTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _selectedNode == null) return;
        if (_selectedNode.Kind == TriggerNodeKind.RootScript ||
            _selectedNode.Kind == TriggerNodeKind.MapHeader ||
            _selectedNode.Kind == TriggerNodeKind.Category)
        {
            MessageBox.Show(this, "Solo puedes duplicar triggers o scripts individuales.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var baseName = _selectedNode.Name;
        int suffix = 2;
        string candidate = $"{baseName}_Copia";
        while (_project.AllTriggerNodes.Any(n => string.Equals(n.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName}_Copia{suffix++}";
        }

        var copy = _selectedNode.Clone(candidate);
        if (copy.Kind == TriggerNodeKind.GuiTrigger)
        {
            copy.Code = copy.GenerateJassFromGui();
        }
        else if (!string.IsNullOrEmpty(copy.Code))
        {
            copy.Code = copy.Code.Replace(baseName, candidate, StringComparison.Ordinal);
            JassKnowledgeService.SyncNodeCodeToProject(_project, copy, string.Empty, copy.Code);
        }

        InsertTriggerIntoTree(copy);
        SelectTriggerNode(copy);
        RefreshMapRefsList();
    }

    private void InsertTriggerIntoTree(TriggerNode node)
    {
        if (_project == null) return;
        var targetCategory = _selectedNode?.Kind == TriggerNodeKind.Category
            ? _selectedNode
            : _project.TriggerTree.FirstOrDefault(t => t.Kind == TriggerNodeKind.Category);

        if (targetCategory != null)
            targetCategory.Children.Add(node);
        else
            _project.TriggerTree.Add(node);

        if (!_project.AllTriggerNodes.Contains(node))
            _project.AllTriggerNodes.Add(node);
    }

    private void BtnNewCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null)
        {
            MessageBox.Show(this, "Debe abrir un mapa para crear categorías.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var count = _project.TriggerTree.Count(t => t.Kind == TriggerNodeKind.Category) + 1;
        var cat = new TriggerNode
        {
            Name = $"Nueva Categoría {count}",
            Kind = TriggerNodeKind.Category
        };
        _project.TriggerTree.Add(cat);
        SelectTriggerNode(cat);
    }

    private void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _selectedNode == null) return;

        if (_selectedNode.Kind == TriggerNodeKind.RootScript || _selectedNode.Kind == TriggerNodeKind.MapHeader)
        {
            MessageBox.Show(this, "No se puede eliminar el script raíz ni la cabecera del mapa.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var res = MessageBox.Show(
            this,
            $"¿Está seguro de que desea eliminar '{_selectedNode.Name}'?",
            "Confirmar eliminación",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes) return;

        RemoveNodeRecursively(_project.TriggerTree, _selectedNode);
        _project.AllTriggerNodes.Remove(_selectedNode);
        ClearDetails();
        _selectedNode = null;
        RefreshMapRefsList();
    }

    private static bool RemoveNodeRecursively(ObservableCollection<TriggerNode> list, TriggerNode target)
    {
        if (list.Remove(target)) return true;
        foreach (var parent in list)
        {
            if (RemoveNodeRecursively(parent.Children, target))
                return true;
        }
        return false;
    }

    // ==================== CÓDIGO IDE, FORMATO, LEAKS Y COMPILACIÓN ====================

    private void CodeEditor_TextChanged(object sender, EventArgs e)
    {
        if (_isUpdatingUi || _selectedNode == null) return;

        var oldCode = _selectedNode.Code;
        var newCode = CodeEditor.Text;
        if (string.Equals(oldCode, newCode, StringComparison.Ordinal))
            return;

        _selectedNode.Code = newCode;
        _selectedNode.IsModified = true;
        JassKnowledgeService.SyncNodeCodeToProject(_project, _selectedNode, oldCode, newCode);

        _liveAnalyzeTimer.Stop();
        _liveAnalyzeTimer.Start();
    }

    private void LiveAnalyzeTimer_Tick(object? sender, EventArgs e)
    {
        _liveAnalyzeTimer.Stop();
        RunLiveLeakAnalysis();
    }

    private void RunLiveLeakAnalysis()
    {
        if (_selectedNode == null || RbCodeMode.IsChecked != true)
            return;

        var code = CodeEditor.Text;
        var leaks = JassKnowledgeService.AnalyzeLeaks(code, _selectedNode.Name);

        _diagnostics.Clear();
        foreach (var d in leaks)
            _diagnostics.Add(d);

        _ideAttachment.UpdateDiagnostics(_diagnostics);

        int errors = leaks.Count(d => d.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
        int warnings = leaks.Count(d => d.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
        int infos = leaks.Count(d => d.Severity.Equals("Info", StringComparison.OrdinalIgnoreCase));

        TxtDiagnosticsSummary.Text = leaks.Count == 0
            ? "Sin leaks ni incompatibilidades WC3 1.27b detectadas • Ctrl+Space autocompletado • ( para parámetros"
            : $"Análisis en vivo: {errors} error(es), {warnings} leak(s)/advertencia(s), {infos} sugerencia(s) — Doble clic para ir a la línea";
    }

    private void BtnFormatCode_Click(object sender, RoutedEventArgs e)
    {
        if (RbGuiMode.IsChecked == true)
            RbCodeMode.IsChecked = true;
        _ideAttachment.FormatDocument();
    }

    private void BtnCommentToggle_Click(object sender, RoutedEventArgs e)
    {
        if (RbCodeMode.IsChecked == true)
            _ideAttachment.ToggleLineComments();
    }

    private void BtnGoToLine_Click(object sender, RoutedEventArgs e)
    {
        if (RbCodeMode.IsChecked == true)
            _ideAttachment.ShowGoToLineDialog();
    }

    private void TxtCodeSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            FindInCode();
    }

    private void BtnFindInCode_Click(object sender, RoutedEventArgs e) => FindInCode();

    private void BtnFocusCodeSearch_Click(object sender, RoutedEventArgs e)
    {
        RbCodeMode.IsChecked = true;
        TxtCodeSearch.Focus();
        TxtCodeSearch.SelectAll();
    }

    private void FindInCode()
    {
        var needle = TxtCodeSearch.Text;
        if (string.IsNullOrEmpty(needle)) return;

        var fullText = CodeEditor.Text;
        var startIdx = CodeEditor.CaretOffset;
        var nextIdx = fullText.IndexOf(needle, startIdx, StringComparison.OrdinalIgnoreCase);

        if (nextIdx < 0)
            nextIdx = fullText.IndexOf(needle, 0, StringComparison.OrdinalIgnoreCase);

        if (nextIdx >= 0)
        {
            CodeEditor.Select(nextIdx, needle.Length);
            CodeEditor.ScrollToLine(CodeEditor.Document.GetLineByOffset(nextIdx).LineNumber);
            CodeEditor.Focus();
        }
    }

    private async void BtnValidateScript_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;

        var activeCode = _selectedNode?.Kind == TriggerNodeKind.GuiTrigger && !_selectedNode.IsCustomText
            ? _selectedNode.GenerateJassFromGui()
            : (!string.IsNullOrWhiteSpace(_selectedNode?.Code) ? _selectedNode.Code : _project.RawMapScript);

        var activeLabel = _selectedNode?.Name ?? _project.FileName;

        // 1. Live WC3 1.27b + Leak detection on active trigger
        var leakResults = JassKnowledgeService.AnalyzeLeaks(activeCode, activeLabel);

        // 2. Full map syntax compilation check with pjass / clijasshelper
        JassKnowledgeService.SyncGlobalVariablesToScript(_project);
        var fullScript = !string.IsNullOrWhiteSpace(_project.RawMapScript) ? _project.RawMapScript : activeCode;
        var compileRes = await _compilerService.ValidateAndCompileAsync(fullScript, _settings, _project.FileName);

        _diagnostics.Clear();
        foreach (var d in compileRes.Diagnostics)
            _diagnostics.Add(d);
        foreach (var l in leakResults)
            _diagnostics.Add(l);

        if (RbCodeMode.IsChecked == true)
            _ideAttachment.UpdateDiagnostics(leakResults.Concat(compileRes.Diagnostics));

        int errCount = _diagnostics.Count(d => d.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
        int warnCount = _diagnostics.Count(d => !d.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
        TxtDiagnosticsSummary.Text = $"Validación completa: {errCount} error(es) de compilación, {warnCount} advertencia(s)/leak(s).";

        if (compileRes.Success && errCount == 0 && warnCount == 0)
        {
            MessageBox.Show(this, "Validación WC3 1.27b limpia: 0 errores de sintaxis y 0 memory leaks detectados.", "Validación Correcta", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (compileRes.Success && errCount == 0)
        {
            RbCodeMode.IsChecked = true;
            MessageBox.Show(this, $"Sintaxis WC3 1.27b correcta (0 errores).\nSe detectaron {warnCount} advertencia(s) de posibles Memory Leaks en el panel inferior.", "Validación con Advertencias de Leak", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            RbCodeMode.IsChecked = true;
        }
    }

    private void GridDiagnostics_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridDiagnostics.SelectedItem is not CompilerDiagnostic diag) return;
        if (diag.Line > 0 && diag.Line <= CodeEditor.LineCount)
        {
            var lineObj = CodeEditor.Document.GetLineByNumber(diag.Line);
            CodeEditor.Select(lineObj.Offset, lineObj.Length);
            CodeEditor.ScrollToLine(diag.Line);
            CodeEditor.Focus();
        }
    }

    private void TxtTreeFilter_TextChanged(object sender, RoutedEventArgs e)
    {
        var filter = TxtTreeFilter?.Text.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(filter))
        {
            if (TreeTriggers != null)
                TreeTriggers.ItemsSource = _project?.TriggerTree;
            return;
        }

        if (_project == null) return;
        bool searchCode = ChkFilterInsideCode?.IsChecked == true;
        var filtered = _project.AllTriggerNodes
            .Where(n => n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        (searchCode && (n.Code?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToList();
        TreeTriggers.ItemsSource = filtered;
    }

    public void UpdateDiagnostics(IEnumerable<CompilerDiagnostic> diagnostics)
    {
        _diagnostics.Clear();
        foreach (var d in diagnostics)
            _diagnostics.Add(d);

        if (RbCodeMode.IsChecked == true)
            _ideAttachment.UpdateDiagnostics(_diagnostics);
    }

    private async void BtnSaveAndCompile_Click(object sender, RoutedEventArgs e)
    {
        if (_project != null)
            JassKnowledgeService.SyncGlobalVariablesToScript(_project);

        if (RequestSaveAndCompile != null)
        {
            var ok = await RequestSaveAndCompile.Invoke();
            if (ok)
                TxtStatusNodeInfo.Text = "Mapa compilado y guardado con éxito. 0 errores.";
        }
        else
        {
            BtnValidateScript_Click(sender, e);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.F7)
        {
            e.Handled = true;
            BtnSaveAndCompile_Click(sender, e);
        }
        else if (e.Key == Key.F8)
        {
            e.Handled = true;
            BtnValidateScript_Click(sender, e);
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.T)
        {
            e.Handled = true;
            BtnNewGuiTrigger_Click(sender, e);
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.U)
        {
            e.Handled = true;
            BtnNewVjassTrigger_Click(sender, e);
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.D)
        {
            e.Handled = true;
            BtnDuplicateTrigger_Click(sender, e);
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            BtnFocusCodeSearch_Click(sender, e);
        }
    }

    // ==================== PANEL DERECHO: NATIVAS / VARIABLES / MAPA ====================

    private void BtnToggleInspector_Click(object sender, RoutedEventArgs e)
    {
        bool show = BtnToggleInspector.IsChecked == true;
        ColRightDrawer.Width = show ? new GridLength(340) : new GridLength(0);
        ColDrawerSplitter.Width = show ? new GridLength(4) : new GridLength(0);
    }

    private void TxtNativeSearch_TextChanged(object sender, RoutedEventArgs e) => RefreshNativesList();

    private void RefreshNativesList()
    {
        if (ListNatives == null || CboNativeKind == null) return;

        var query = TxtNativeSearch?.Text.Trim() ?? string.Empty;
        var kindFilter = (CboNativeKind.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";

        var all = JassKnowledgeService.GetSdkSymbols(_settings);
        var filtered = all.Where(s =>
        {
            if (kindFilter == "All")
            {
                if (s.Kind == "Keyword" || s.Kind == "Type") return false;
            }
            else if (!string.Equals(s.Kind, kindFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrEmpty(query)) return true;
            return s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   s.Signature.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   s.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
        })
        .Take(350)
        .ToList();

        ListNatives.ItemsSource = filtered;
    }

    private void ListNatives_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListNatives.SelectedItem is JassSymbol sym)
        {
            TxtNativeSignature.Text = string.IsNullOrWhiteSpace(sym.Signature) ? sym.Name : sym.Signature;
            TxtNativeDesc.Text = $"[{sym.DisplayKind}] {sym.Description}";
        }
    }

    private void ListNatives_MouseDoubleClick(object sender, MouseButtonEventArgs e) => InsertSelectedNative();
    private void BtnInsertSelectedNative_Click(object sender, RoutedEventArgs e) => InsertSelectedNative();

    private void InsertSelectedNative()
    {
        if (ListNatives.SelectedItem is not JassSymbol sym) return;
        var textToInsert = !string.IsNullOrEmpty(sym.InsertTemplate)
            ? sym.InsertTemplate
            : (sym.Kind is "Native" or "BJ" ? $"{sym.Name}(" : sym.Name);
        InsertSnippetIntoCodeEditor(textToInsert);
    }

    private void RefreshVariablesGrid()
    {
        _variablesView.Clear();
        if (_project == null) return;
        foreach (var gv in _project.GlobalVariables)
            _variablesView.Add(gv);
    }

    private void BtnNewVariable_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        _editingVariable = null;
        TxtVarFormTitle.Text = "Nueva Variable Global (udg_*)";
        TxtVarName.Text = $"MiVariable_{_project.GlobalVariables.Count + 1}";
        CboVarType.Text = "integer";
        ChkVarIsArray.IsChecked = false;
        TxtVarInitVal.Text = "0";
        PanelVarEditor.Visibility = Visibility.Visible;
        TxtVarName.Focus();
    }

    private void BtnEditVariable_Click(object sender, RoutedEventArgs e)
    {
        if (GridVariables.SelectedItem is not W3GlobalVariable gv) return;
        _editingVariable = gv;
        TxtVarFormTitle.Text = $"Editar Variable '{gv.JassIdentifier}'";
        TxtVarName.Text = gv.Name;
        CboVarType.Text = gv.Type;
        ChkVarIsArray.IsChecked = gv.IsArray;
        TxtVarInitVal.Text = gv.InitialValue;
        PanelVarEditor.Visibility = Visibility.Visible;
        TxtVarName.Focus();
    }

    private void BtnSaveVariable_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        var cleanVarName = System.Text.RegularExpressions.Regex.Replace(TxtVarName.Text.Trim(), @"[^A-Za-z0-9_]", "_");
        if (cleanVarName.StartsWith("udg_", StringComparison.OrdinalIgnoreCase))
            cleanVarName = cleanVarName[4..];

        if (string.IsNullOrWhiteSpace(cleanVarName))
        {
            MessageBox.Show(this, "Ingresa un nombre válido para la variable.", "Variable inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var varType = string.IsNullOrWhiteSpace(CboVarType.Text) ? "integer" : CboVarType.Text.Trim();
        var isArray = ChkVarIsArray.IsChecked == true;
        var initVal = TxtVarInitVal.Text.Trim();

        if (_editingVariable == null)
        {
            if (_project.GlobalVariables.Any(v => string.Equals(v.Name, cleanVarName, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, $"Ya existe la variable udg_{cleanVarName}.", "Duplicado", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _project.GlobalVariables.Add(new W3GlobalVariable
            {
                Name = cleanVarName,
                Type = varType,
                IsArray = isArray,
                ArraySize = 1,
                InitialValue = initVal
            });
        }
        else
        {
            _editingVariable.Name = cleanVarName;
            _editingVariable.Type = varType;
            _editingVariable.IsArray = isArray;
            _editingVariable.InitialValue = initVal;
        }

        JassKnowledgeService.SyncGlobalVariablesToScript(_project);
        _project.HasMiscEdits = true;
        PanelVarEditor.Visibility = Visibility.Collapsed;
        RefreshVariablesGrid();
        RefreshMapRefsList();
    }

    private void BtnCancelVariable_Click(object sender, RoutedEventArgs e)
    {
        PanelVarEditor.Visibility = Visibility.Collapsed;
    }

    private void BtnDeleteVariable_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || GridVariables.SelectedItem is not W3GlobalVariable gv) return;
        var res = MessageBox.Show(this, $"¿Eliminar variable '{gv.JassIdentifier}' de war3map.j?", "Confirmar eliminación", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res != MessageBoxResult.Yes) return;

        _project.GlobalVariables.Remove(gv);
        JassKnowledgeService.SyncGlobalVariablesToScript(_project);
        _project.HasMiscEdits = true;
        RefreshVariablesGrid();
        RefreshMapRefsList();
    }

    private void GridVariables_MouseDoubleClick(object sender, MouseButtonEventArgs e) => InsertSelectedVariable();
    private void BtnInsertVariable_Click(object sender, RoutedEventArgs e) => InsertSelectedVariable();

    private void InsertSelectedVariable()
    {
        if (GridVariables.SelectedItem is not W3GlobalVariable gv) return;
        InsertSnippetIntoCodeEditor(gv.IsArray ? $"{gv.JassIdentifier}[0]" : gv.JassIdentifier);
    }

    private void TxtMapRefSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshMapRefsList();

    private void RefreshMapRefsList()
    {
        if (ListMapRefs == null) return;
        var items = new List<JassSymbol>();
        if (_project != null)
        {
            items.AddRange(JassKnowledgeService.GetProjectSymbols(_project));
            foreach (var obj in _project.Objects.Take(300))
            {
                if (string.IsNullOrWhiteSpace(obj.Rawcode)) continue;
                items.Add(new JassSymbol
                {
                    Name = $"'{obj.Rawcode}'",
                    Kind = obj.Category.ToString(),
                    Description = $"{obj.Name} ({obj.Category})",
                    Signature = $"'{obj.Rawcode}'"
                });
            }
        }

        var query = TxtMapRefSearch?.Text.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(query))
        {
            items = items
                .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            s.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        ListMapRefs.ItemsSource = items.Take(350).ToList();
    }

    private void ListMapRefs_MouseDoubleClick(object sender, MouseButtonEventArgs e) => InsertSelectedMapRef();
    private void BtnInsertSelectedMapRef_Click(object sender, RoutedEventArgs e) => InsertSelectedMapRef();

    private void InsertSelectedMapRef()
    {
        if (ListMapRefs.SelectedItem is not JassSymbol sym) return;
        InsertSnippetIntoCodeEditor(sym.Name);
    }

    private void InsertSnippetIntoCodeEditor(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (_selectedNode == null) return;
        if (RbGuiMode.IsChecked == true)
            RbCodeMode.IsChecked = true;

        int caret = CodeEditor.CaretOffset;
        CodeEditor.Document.Insert(caret, text);
        CodeEditor.CaretOffset = caret + text.Length;
        CodeEditor.Focus();
    }

    private void MenuClose_Click(object sender, RoutedEventArgs e) => Close();
    private void MenuOpenObjectEditor_Click(object sender, RoutedEventArgs e) => RequestOpenObjectEditor?.Invoke();
}
