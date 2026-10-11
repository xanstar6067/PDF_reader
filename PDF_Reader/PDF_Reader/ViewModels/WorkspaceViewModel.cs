using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PDF_Reader.Models;
using PDF_Reader.Services;

namespace PDF_Reader.ViewModels;

public sealed class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IUserDialogs _dialogs;
    private readonly SettingsService _settings;
    private readonly RecoveryService _recovery;
    private DocumentTab? _activeTab;
    private bool _busy, _searching, _matchCase, _wholeWord, _fitWidth, _printing, _autosaving;
    private int _currentPage = 1, _navigationTab, _matchesVersion;
    private double _zoom = 1, _viewportWidth = 900;
    private string _status = "Откройте PDF, чтобы начать работу", _query = "";
    private DocumentTool _tool;
    private SearchMatch? _selectedMatch;
    private PdfComment? _selectedAnnotation;
    private float _strokeWidth = 2;
    private readonly Dictionary<DocumentTool, Color> _toolColors = new()
    {
        [DocumentTool.Highlight] = PdfDocument.DefaultHighlight,
        [DocumentTool.Underline] = Color.FromRgb(67, 160, 71),
        [DocumentTool.StrikeOut] = Color.FromRgb(229, 57, 53),
        [DocumentTool.Rectangle] = Color.FromRgb(229, 57, 53),
        [DocumentTool.Ellipse] = Color.FromRgb(229, 57, 53),
        [DocumentTool.Ink] = Color.FromRgb(30, 136, 229)
    };
    private CancellationTokenSource? _searchCancellation;
    private Dictionary<int, List<SearchMatch>> _pageMatches = [];

    public event Action<int>? NavigateRequested;
    public event Action? FindRequested;
    public event Action? PrintRequested;
    public event Action? DefaultAppRequested;

    public ObservableCollection<DocumentTab> Documents { get; } = [];
    public ObservableCollection<PageItem> Pages { get; } = [];
    public ObservableCollection<OutlineNode> Outline { get; } = [];
    public ObservableCollection<SearchMatch> Matches { get; } = [];
    public ObservableCollection<PdfComment> Comments { get; } = [];
    public ObservableCollection<RecentFile> RecentFiles { get; } = [];
    public DocumentInfo? Info { get; private set; }

    public DocumentTab? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (IsBusy || !Set(ref _activeTab, value)) return;
            ActivateDocumentAsync();
        }
    }

    public PdfDocument? Document => ActiveTab?.Document;
    public bool HasDocument => Document is not null;
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            Set(ref _busy, value);
            Notify(nameof(CanInteract));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool CanInteract => HasDocument && !IsBusy;
    public bool IsPrinting
    {
        get => _printing;
        set
        {
            Set(ref _printing, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsSearching
    {
        get => _searching;
        private set
        {
            Set(ref _searching, value);
            Notify(nameof(SearchSummary));
        }
    }
    public string WindowTitle => (ActiveTab?.Title is { } title ? title + " — " : "") + "PaperDesk PDF";
    public string Status { get => _status; set => Set(ref _status, value); }
    public int PageCount => Pages.Count;

    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            var page = Math.Clamp(value, 1, Math.Max(1, PageCount));
            if (!Set(ref _currentPage, page)) return;
            if (ActiveTab is not null) ActiveTab.LastPage = page;
            NavigateRequested?.Invoke(page - 1);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public void SetVisiblePage(int index)
    {
        if (index < 0 || index >= PageCount) return;
        if (!Set(ref _currentPage, index + 1, nameof(CurrentPage))) return;
        if (ActiveTab is not null) ActiveTab.LastPage = _currentPage;
        CommandManager.InvalidateRequerySuggested();
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            var zoom = double.IsFinite(value) ? Math.Clamp(value, .25, 4) : 1;
            if (!Set(ref _zoom, zoom)) return;
            foreach (var page in Pages) page.Zoom = zoom;
            Notify(nameof(ZoomPercent));
            _settings.Value.Zoom = zoom;
            if (HasDocument) NavigateRequested?.Invoke(CurrentPage - 1);
        }
    }

    public int ZoomPercent
    {
        get => (int)Math.Round(Zoom * 100);
        set
        {
            _fitWidth = false;
            Zoom = value / 100.0;
        }
    }

    public DocumentTool Tool
    {
        get => _tool;
        set
        {
            if (!Set(ref _tool, value)) return;
            if (value != DocumentTool.Select) SelectedAnnotation = null;
            Notify(nameof(ToolHint));
            Notify(nameof(CurrentColor));
            Status = ToolHint;
        }
    }

    public string ToolHint => Tool switch
    {
        DocumentTool.EditText => "Нажмите на текстовый фрагмент, чтобы изменить его",
        DocumentTool.AddText => "Нажмите на страницу, чтобы добавить текст",
        DocumentTool.Highlight => "Протяните мышью по тексту, чтобы выделить его маркером",
        DocumentTool.Underline => "Протяните мышью по тексту, чтобы подчеркнуть его",
        DocumentTool.StrikeOut => "Протяните мышью по тексту, чтобы зачеркнуть его",
        DocumentTool.Note => "Нажмите на страницу, чтобы оставить заметку",
        DocumentTool.Rectangle => "Протяните мышью, чтобы нарисовать прямоугольник",
        DocumentTool.Ellipse => "Протяните мышью, чтобы нарисовать овал",
        DocumentTool.Ink => "Рисуйте мышью с нажатой кнопкой",
        _ => "Выделяйте текст мышью • щелчок по аннотации выделяет её • Delete — удалить • Ctrl+колесо — масштаб"
    };

    public static IReadOnlyList<Color> Palette { get; } =
    [
        PdfDocument.DefaultHighlight, Color.FromRgb(67, 160, 71), Color.FromRgb(30, 136, 229),
        Color.FromRgb(229, 57, 53), Color.FromRgb(142, 36, 170), Color.FromRgb(33, 33, 33)
    ];

    public static IReadOnlyList<float> StrokeWidths { get; } = [1f, 2f, 4f];

    /// <summary>Line width in points for new rectangles, ellipses and drawings.</summary>
    public float StrokeWidth { get => _strokeWidth; set => Set(ref _strokeWidth, value); }

    /// <summary>Colour of the selected annotation, or of new annotations made with the current tool.</summary>
    public Color CurrentColor
    {
        get => SelectedAnnotation is { CanRecolor: true } annotation ? annotation.Color : _toolColors.GetValueOrDefault(Tool, Palette[3]);
        set
        {
            if (SelectedAnnotation is { CanRecolor: true } annotation)
            {
                // The panel updates once the document has the new colour.
                if (annotation.Color != value) RecolorAsync(annotation, value);
                return;
            }
            _toolColors[Tool] = value;
            Notify();
        }
    }

    public PdfComment? SelectedAnnotation
    {
        get => _selectedAnnotation;
        set
        {
            if (!Set(ref _selectedAnnotation, value)) return;
            Notify(nameof(HasSelectedAnnotation));
            Notify(nameof(SelectedAnnotationSummary));
            Notify(nameof(CurrentColor));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelectedAnnotation => SelectedAnnotation is not null;

    public string SelectedAnnotationSummary => SelectedAnnotation is { } a
        ? $"{a.Kind}, стр. {a.PageNumber}" + (a.CanMove ? " • перетаскивайте мышью" : "")
        : "";

    public PrintScaling PrintScaling
    {
        get => _settings.Value.PrintScaling;
        set
        {
            if (_settings.Value.PrintScaling == value) return;
            _settings.Value.PrintScaling = value;
            _settings.TrySave();
            Notify();
        }
    }

    public int NavigationTab { get => _navigationTab; set => Set(ref _navigationTab, value); }
    public string Query { get => _query; set { if (Set(ref _query, value)) QueueSearch(); } }
    public bool MatchCase { get => _matchCase; set { if (Set(ref _matchCase, value)) QueueSearch(); } }
    public bool WholeWord { get => _wholeWord; set { if (Set(ref _wholeWord, value)) QueueSearch(); } }

    public SearchMatch? SelectedMatch
    {
        get => _selectedMatch;
        set
        {
            if (!Set(ref _selectedMatch, value)) return;
            Notify(nameof(SearchSummary));
            if (value is null) return;
            CurrentPage = value.PageNumber;
            NavigateRequested?.Invoke(value.PageIndex);
        }
    }

    /// <summary>Changes when the set of search results changes, so page views redraw their highlights.</summary>
    public int MatchesVersion => _matchesVersion;

    public IReadOnlyList<SearchMatch> GetPageMatches(int index) => _pageMatches.TryGetValue(index, out var list) ? list : [];

    public string SearchSummary => IsSearching ? "Поиск…" : string.IsNullOrWhiteSpace(Query) ? "Введите текст для поиска" :
        Matches.Count == 0 ? "Совпадений нет" :
        $"{(SelectedMatch is null ? 0 : Matches.IndexOf(SelectedMatch) + 1)} / {Matches.Count}{(Matches.Count == 10000 ? "+" : "")} совпадений";

    public string DocumentSummary => Info is null ? "" :
        $"{Info.Pages} стр. • {(Info.IsEncrypted ? "Защищённый PDF" : "PDF документ")}" +
        (Info.Signatures > 0 ? " • Есть цифровая подпись: изменения сохраняются инкрементально" : "") +
        (Info.CanEdit ? "" : " • Редактирование запрещено");

    public ICommand OpenCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand PrintCommand { get; }
    public ICommand SetPrintScalingCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand FirstPageCommand { get; }
    public ICommand LastPageCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ActualSizeCommand { get; }
    public ICommand FitWidthCommand { get; }
    public ICommand FindCommand { get; }
    public ICommand NextMatchCommand { get; }
    public ICommand PreviousMatchCommand { get; }
    public ICommand RotateCommand { get; }
    public ICommand DeletePageCommand { get; }
    public ICommand MovePageUpCommand { get; }
    public ICommand MovePageDownCommand { get; }
    public ICommand InsertPageCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CopyPageTextCommand { get; }
    public ICommand DeleteAnnotationCommand { get; }
    public ICommand EditAnnotationTextCommand { get; }
    public ICommand PropertiesCommand { get; }
    public ICommand DefaultAppCommand { get; }

    public WorkspaceViewModel(IUserDialogs dialogs, SettingsService settings, RecoveryService? recovery = null)
    {
        _dialogs = dialogs;
        _settings = settings;
        _recovery = recovery ?? new RecoveryService();
        _zoom = settings.Value.Zoom;
        foreach (var file in settings.Value.RecentFiles) RecentFiles.Add(file);

        bool Available() => CanInteract;
        bool CanAssemble() => CanInteract && Info?.CanAssemble == true;
        // History and dirty state are cached by PdfDocument, so these checks never wait for a render.
        OpenCommand = Async(OpenAsync, () => !IsBusy);
        NewCommand = Async(() => BusyAsync("Создание PDF…", async () => await AddDocumentAsync(await Task.Run(PdfDocument.Create))), () => !IsBusy);
        SaveCommand = Async(() => SaveAsync(ActiveTab!, false), Available);
        SaveAsCommand = Async(() => SaveAsync(ActiveTab!, true), Available);
        CloseCommand = Async(async () => { if (ActiveTab is { } tab) await CloseTabAsync(tab); }, Available);
        PrintCommand = new RelayCommand(() => PrintRequested?.Invoke(), () => CanInteract && !IsPrinting && Info?.CanPrint == true);
        SetPrintScalingCommand = new RelayCommand<string>(value => PrintScaling = Enum.Parse<PrintScaling>(value));
        UndoCommand = Async(() => ChangeAsync(d => d.Undo()), () => CanInteract && Document!.CanUndo);
        RedoCommand = Async(() => ChangeAsync(d => d.Redo()), () => CanInteract && Document!.CanRedo);
        PreviousPageCommand = new RelayCommand(() => CurrentPage--, () => CanInteract && CurrentPage > 1);
        NextPageCommand = new RelayCommand(() => CurrentPage++, () => CanInteract && CurrentPage < PageCount);
        FirstPageCommand = new RelayCommand(() => CurrentPage = 1, Available);
        LastPageCommand = new RelayCommand(() => CurrentPage = PageCount, Available);
        ZoomInCommand = new RelayCommand(() => { _fitWidth = false; Zoom *= 1.15; }, Available);
        ZoomOutCommand = new RelayCommand(() => { _fitWidth = false; Zoom /= 1.15; }, Available);
        ActualSizeCommand = new RelayCommand(() => { _fitWidth = false; Zoom = 1; }, Available);
        FitWidthCommand = new RelayCommand(() => { _fitWidth = true; FitWidth(); }, Available);
        FindCommand = new RelayCommand(() => { NavigationTab = 2; FindRequested?.Invoke(); }, Available);
        NextMatchCommand = new RelayCommand(() => MoveMatch(1), () => Matches.Count > 0);
        PreviousMatchCommand = new RelayCommand(() => MoveMatch(-1), () => Matches.Count > 0);
        RotateCommand = Async(() => ChangeAsync(d => d.RotatePage(CurrentPage - 1, 1), CurrentPage - 1), CanAssemble);
        DeletePageCommand = Async(async () =>
        {
            if (_dialogs.Confirm("Удалить страницу", $"Удалить страницу {CurrentPage}? Действие можно отменить."))
                await ChangeAsync(d => d.DeletePage(CurrentPage - 1));
        }, () => CanAssemble() && PageCount > 1);
        MovePageUpCommand = Async(() => MovePageAsync(-1), () => CanAssemble() && CurrentPage > 1);
        MovePageDownCommand = Async(() => MovePageAsync(1), () => CanAssemble() && CurrentPage < PageCount);
        InsertPageCommand = Async(() => ChangeAsync(d => d.InsertBlankPage(CurrentPage)), CanAssemble);
        ImportCommand = Async(ImportAsync, CanAssemble);
        ExportCommand = Async(ExportAsync, () => CanInteract && Info?.CanCopy == true);
        CopyPageTextCommand = Async(CopyPageTextAsync, () => CanInteract && Info?.CanCopy == true);
        bool CanAnnotate() => CanInteract && Info?.CanAnnotate == true;
        DeleteAnnotationCommand = Async(() => SelectedAnnotation is { } a ? DeleteCommentAsync(a) : Task.CompletedTask,
            () => CanAnnotate() && SelectedAnnotation is not null);
        EditAnnotationTextCommand = Async(EditAnnotationTextAsync, () => CanAnnotate() && SelectedAnnotation?.CanEditText == true);
        PropertiesCommand = new RelayCommand(ShowProperties, Available);
        DefaultAppCommand = new RelayCommand(() => DefaultAppRequested?.Invoke(), () => !IsBusy);
    }

    private AsyncCommand Async(Func<Task> action, Func<bool>? canExecute = null) => new(action, ShowError, canExecute);

    public void ShowError(Exception error)
    {
        Status = error.Message;
        _dialogs.Error(error);
    }

    private async Task BusyAsync(string status, Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = status;
        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void ActivateDocumentAsync()
    {
        CancelSearch();
        _tool = DocumentTool.Select;
        Notify(nameof(Tool));
        Notify(nameof(CurrentColor));
        try
        {
            await BusyAsync("Загрузка документа…", () => RefreshAsync());
            if (!string.IsNullOrWhiteSpace(Query)) QueueSearch();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private record WorkspaceData(DocumentInfo Info, IReadOnlyList<PdfPageInfo> Pages, IReadOnlyList<OutlineNode>? Outline, IReadOnlyList<PdfComment> Comments);

    /// <summary>
    /// Synchronizes the panels with the document. After a change confined to <paramref name="changedPage"/>
    /// only that page's annotations are re-read, page views are reused and the scroll position is kept.
    /// </summary>
    private async Task RefreshAsync(int? changedPage = null)
    {
        var document = Document;
        var data = document is null ? null : await Task.Run(() => new WorkspaceData(document.GetInfo(), document.GetPages(),
            changedPage is null ? document.GetOutline() : null, document.GetComments(changedPage)));
        var selected = SelectedAnnotation;
        SetMatches([]);
        Info = data?.Info;
        if (document is not null && data is not null)
        {
            UpdatePages(document, data.Pages);
            if (data.Outline is not null)
            {
                Outline.Clear();
                foreach (var node in data.Outline) Outline.Add(node);
            }
            if (changedPage is { } page) ReplaceComments(page, data.Comments);
            else
            {
                Comments.Clear();
                foreach (var comment in data.Comments) Comments.Add(comment);
            }
            // An edit of the selected annotation keeps it selected with its new geometry and colour.
            SelectedAnnotation = selected is null || changedPage is null ? null
                : changedPage != selected.PageIndex ? selected
                : Comments.FirstOrDefault(c => c.PageIndex == selected.PageIndex && c.AnnotationIndex == selected.AnnotationIndex);
            _currentPage = Math.Clamp(ActiveTab!.LastPage, 1, PageCount);
            ActiveTab.Refresh();
            if (_fitWidth) FitWidth();
            Status = ToolHint;
        }
        else
        {
            Pages.Clear();
            Outline.Clear();
            Comments.Clear();
            SelectedAnnotation = null;
            _currentPage = 1;
            Status = "Откройте PDF, чтобы начать работу";
        }
        foreach (var name in new[] { nameof(HasDocument), nameof(CanInteract), nameof(Document), nameof(PageCount), nameof(CurrentPage),
                     nameof(WindowTitle), nameof(Info), nameof(DocumentSummary), nameof(SearchSummary) })
            Notify(name);
        CommandManager.InvalidateRequerySuggested();
        if (HasDocument && changedPage is null) NavigateRequested?.Invoke(CurrentPage - 1);
    }

    private void UpdatePages(PdfDocument document, IReadOnlyList<PdfPageInfo> pages)
    {
        if (Pages.Count == 0 || Pages[0].Document != document)
        {
            Pages.Clear();
            foreach (var page in pages) Pages.Add(new(document, page, Zoom));
            return;
        }
        // Keep existing items so the list does not recreate its containers; changed geometry gets a new item.
        for (var i = 0; i < pages.Count; i++)
        {
            if (i >= Pages.Count) Pages.Add(new(document, pages[i], Zoom));
            else if (Pages[i].Info == pages[i]) Pages[i].Invalidate();
            else Pages[i] = new(document, pages[i], Zoom);
        }
        while (Pages.Count > pages.Count) Pages.RemoveAt(Pages.Count - 1);
    }

    private void ReplaceComments(int page, IReadOnlyList<PdfComment> comments)
    {
        foreach (var old in Comments.Where(c => c.PageIndex == page).ToList()) Comments.Remove(old);
        var position = Comments.TakeWhile(c => c.PageIndex < page).Count();
        foreach (var comment in comments) Comments.Insert(position++, comment);
    }

    private async Task AddDocumentAsync(PdfDocument document)
    {
        var tab = new DocumentTab(document, _recovery.NewId());
        if (document.FilePath is { } path)
        {
            tab.LastPage = _settings.Value.RecentFiles.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))?.Page ?? 1;
            _settings.Touch(path, tab.LastPage);
            ReloadRecents();
        }
        Documents.Add(tab);
        _activeTab = tab;
        Notify(nameof(ActiveTab));
        await RefreshAsync();
    }

    private void ReloadRecents()
    {
        RecentFiles.Clear();
        foreach (var file in _settings.Value.RecentFiles) RecentFiles.Add(file);
    }

    public async Task OpenAsync()
    {
        foreach (var path in _dialogs.ChooseOpenFiles()) await OpenPathAsync(path);
    }

    public async Task OpenPathAsync(string path)
    {
        if (IsBusy) return;
        var existing = Documents.FirstOrDefault(t => string.Equals(t.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing == ActiveTab) return;
            _activeTab = existing;
            Notify(nameof(ActiveTab));
            CancelSearch();
            Tool = DocumentTool.Select;
            await BusyAsync("Загрузка документа…", () => RefreshAsync());
            return;
        }
        await BusyAsync("Открытие PDF…", async () =>
        {
            var document = await LoadWithPasswordAsync(Path.GetFileName(path), password => PdfDocument.Open(path, password));
            if (document is not null) await AddDocumentAsync(document);
        });
    }

    private async Task<PdfDocument?> LoadWithPasswordAsync(string name, Func<string?, PdfDocument> open)
    {
        string? password = null;
        while (true)
        {
            try
            {
                return await Task.Run(() => open(password));
            }
            catch (PdfPasswordException)
            {
                password = _dialogs.AskText("Защищённый документ", "Введите пароль для открытия «" + name + "».", password: true);
                if (password is null) return null;
            }
        }
    }

    public async Task<bool> SaveAsync(DocumentTab tab, bool saveAs)
    {
        var path = saveAs || tab.Path is null ? _dialogs.ChooseSaveFile(tab.Path is null ? "Документ.pdf" : Path.GetFileName(tab.Path)) : tab.Path;
        if (path is null) return false;
        if (Documents.Any(other => other != tab && string.Equals(other.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Этот файл уже открыт в другой вкладке. Выберите другое имя для сохранения.");
        var saved = false;
        await BusyAsync("Сохранение PDF…", async () =>
        {
            await Task.Run(() => tab.Document.Save(path));
            tab.Refresh();
            Notify(nameof(WindowTitle));
            _settings.Touch(path, tab.LastPage);
            ReloadRecents();
            if (!tab.Document.IsDirty) DeleteRecovery(tab);
            Status = "Документ сохранён";
            saved = true;
        });
        return saved;
    }

    public async Task<bool> CloseTabAsync(DocumentTab tab)
    {
        if (IsBusy) return false;
        if (tab.Document.IsDirty)
        {
            var answer = _dialogs.AskSave(tab.Title.TrimEnd(' ', '•'));
            if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.Yes && !await SaveAsync(tab, false)) return false;
        }
        if (tab.Path is { } path)
        {
            _settings.Touch(path, tab.LastPage);
            ReloadRecents();
        }
        var wasActive = ReferenceEquals(ActiveTab, tab);
        var index = Documents.IndexOf(tab);
        if (wasActive) CancelSearch();
        await BusyAsync("Закрытие документа…", async () =>
        {
            // Suppress selection callbacks until the collection and active document agree.
            Documents.Remove(tab);
            tab.Dispose();
            DeleteRecovery(tab);
            if (wasActive)
            {
                _activeTab = Documents.Count > 0 ? Documents[Math.Min(index, Documents.Count - 1)] : null;
                Notify(nameof(ActiveTab));
                await RefreshAsync();
            }
        });
        if (HasDocument && wasActive && !string.IsNullOrWhiteSpace(Query)) QueueSearch();
        return true;
    }

    public async Task<bool> PrepareCloseAsync()
    {
        if (IsBusy) return false;
        foreach (var tab in Documents.ToArray())
        {
            if (!tab.Document.IsDirty) continue;
            var answer = _dialogs.AskSave(tab.Title.TrimEnd(' ', '•'));
            if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.Yes && !await SaveAsync(tab, false)) return false;
        }
        return true;
    }

    /// <summary>Applies a document change off the UI thread. <paramref name="page"/> names the only page it touches, if any.</summary>
    public async Task ChangeAsync(Action<PdfDocument> change, int? page = null)
    {
        if (!CanInteract || Document is not { } document) return;
        CancelSearch();
        await BusyAsync("Обновление документа…", async () =>
        {
            await Task.Run(() => change(document));
            await RefreshAsync(page);
        });
        if (!string.IsNullOrWhiteSpace(Query)) QueueSearch();
    }

    private async Task MovePageAsync(int direction)
    {
        var from = CurrentPage - 1;
        var to = from + direction;
        await ChangeAsync(d => d.MovePage(from, to));
        CurrentPage = to + 1;
    }

    private async Task ImportAsync()
    {
        var paths = _dialogs.ChooseOpenFiles();
        if (paths.Length == 0 || Document is not { } document) return;
        CancelSearch();
        var index = CurrentPage;
        await BusyAsync("Импорт страниц…", async () =>
        {
            foreach (var path in paths)
            {
                using var source = await LoadWithPasswordAsync(Path.GetFileName(path), password => PdfDocument.Open(path, password));
                if (source is null) continue;
                await Task.Run(() => document.ImportPages(source, index));
                index += source.PageCount;
            }
            await RefreshAsync();
        });
    }

    private async Task ExportAsync()
    {
        var document = Document!;
        var range = _dialogs.AskText("Извлечь страницы", $"Введите номера страниц от 1 до {PageCount}. Пример: 1-3,5.", CurrentPage.ToString());
        if (string.IsNullOrWhiteSpace(range)) return;
        var path = _dialogs.ChooseSaveFile("Страницы.pdf");
        if (path is null) return;
        if (string.Equals(Path.GetFullPath(path), document.FilePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Для извлечённых страниц выберите другой файл.");
        await BusyAsync("Извлечение страниц…", () => Task.Run(() => AtomicFile.Write(path, document.ExportPages(range))));
        Status = "Страницы сохранены в отдельный PDF";
    }

    private async Task CopyPageTextAsync()
    {
        var document = Document!;
        var index = CurrentPage - 1;
        var text = await Task.Run(() => document.ExtractText(index));
        if (string.IsNullOrWhiteSpace(text))
        {
            Status = "На странице нет текстового слоя. Для скана требуется OCR.";
            return;
        }
        Clipboard.SetText(text);
        Status = "Текст страницы скопирован";
    }

    public async Task EditAtAsync(int index, Point point)
    {
        if (!CanInteract) return;
        var document = Document!;
        if (Tool == DocumentTool.EditText)
        {
            if (Info?.CanEdit != true) throw new UnauthorizedAccessException("Редактирование запрещено владельцем PDF.");
            var hit = await Task.Run(() => document.HitText(index, point));
            if (Document != document || !CanInteract) return;
            if (hit is null)
            {
                Status = "Здесь нет редактируемого текста. Для текста на скане требуется OCR.";
                return;
            }
            var value = _dialogs.EditText("Редактировать текст",
                "Изменяется выбранный текстовый объект PDF. Положение, цвет и режим отрисовки сохраняются. Исходный шрифт остаётся, если в нём есть все нужные символы; иначе встраивается Segoe UI. Пустой текст удаляет фрагмент.",
                hit.Text, hit.FontSize, false);
            if (value is null) return;
            await ChangeAsync(d => d.ReplaceText(hit, value.Value.Text, value.Value.Size), index);
        }
        else if (Tool == DocumentTool.AddText)
        {
            var value = _dialogs.EditText("Добавить текст", "Текст будет добавлен в выбранную точку страницы. Поддерживаются несколько строк и кириллица.", "", 12, true);
            if (value is null || string.IsNullOrWhiteSpace(value.Value.Text)) return;
            await ChangeAsync(d => d.AddText(index, point, value.Value.Text, value.Value.Size), index);
        }
        else if (Tool == DocumentTool.Note)
        {
            var text = _dialogs.AskText("Добавить заметку", "Заметка сохраняется в PDF и доступна в других программах просмотра.", multiline: true);
            if (string.IsNullOrWhiteSpace(text)) return;
            await ChangeAsync(d => d.AddNote(index, point, text), index);
            NavigationTab = 3;
        }
    }

    // Annotations --------------------------------------------------------------------------------

    /// <summary>Highlight, underline or strike-out (by the current tool) over text rectangles.</summary>
    public Task AddTextMarkupAsync(int index, IReadOnlyList<Rect> rectangles)
    {
        var type = Tool switch
        {
            DocumentTool.Underline => AnnotationType.Underline,
            DocumentTool.StrikeOut => AnnotationType.StrikeOut,
            _ => AnnotationType.Highlight
        };
        var color = CurrentColor;
        return ChangeAsync(d => d.AddTextMarkup(index, rectangles, type, color), index);
    }

    public Task AddShapeAsync(int index, Rect bounds)
    {
        var type = Tool == DocumentTool.Ellipse ? AnnotationType.Circle : AnnotationType.Square;
        var (color, width) = (CurrentColor, StrokeWidth);
        return ChangeAsync(d => d.AddShape(index, bounds, type, color, width), index);
    }

    public Task AddInkAsync(int index, IReadOnlyList<Point> points)
    {
        var (color, width) = (CurrentColor, StrokeWidth);
        return ChangeAsync(d => d.AddInk(index, points, color, width), index);
    }

    public Task MoveAnnotationAsync(PdfComment annotation, Vector offset) =>
        ChangeAsync(d => d.MoveAnnotation(annotation, offset), annotation.PageIndex);

    public Task ResizeAnnotationAsync(PdfComment annotation, Rect bounds) =>
        ChangeAsync(d => d.ResizeAnnotation(annotation, bounds), annotation.PageIndex);

    private async void RecolorAsync(PdfComment annotation, Color color)
    {
        try
        {
            await ChangeAsync(d => d.SetAnnotationColor(annotation, color), annotation.PageIndex);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        Notify(nameof(CurrentColor));
    }

    public async Task EditAnnotationTextAsync()
    {
        if (SelectedAnnotation is not { CanEditText: true } annotation) return;
        var text = _dialogs.AskText("Комментарий", $"{annotation.Kind}, страница {annotation.PageNumber}. Текст сохраняется в PDF и виден в других программах.",
            annotation.Text, multiline: true);
        if (text is null || text == annotation.Text) return;
        await ChangeAsync(d => d.SetAnnotationText(annotation, text), annotation.PageIndex);
    }

    /// <summary>Topmost annotation under a point of a page; <paramref name="tolerance"/> widens thin shapes.</summary>
    public PdfComment? HitAnnotation(int index, Point point, Size tolerance, bool movableOnly = false)
    {
        PdfComment? hit = null;
        foreach (var comment in Comments)
        {
            if (comment.PageIndex != index || movableOnly && !comment.CanMove) continue;
            var bounds = comment.Bounds;
            bounds.Inflate(tolerance);
            if (bounds.Contains(point)) hit = comment; // Later annotations are drawn on top.
        }
        return hit;
    }

    public async Task<bool> TryFillFormAtAsync(int index, Point point)
    {
        if (!CanInteract || Info?.FormType != 1) return false;
        var document = Document!;
        var field = await Task.Run(() => document.HitFormField(index, point));
        if (Document != document || !CanInteract || field is null) return false;
        if (field.ReadOnly)
        {
            Status = "Это поле формы доступно только для чтения";
            return true;
        }
        if (field.Type is 2 or 3) await ChangeAsync(d => d.FillFormField(field, ""), index);
        else if (field.Type is 4 or 6)
        {
            var value = _dialogs.AskText("Заполнить форму", "Поле: " + field.Name, field.Value, multiline: true);
            if (value is not null) await ChangeAsync(d => d.FillFormField(field, value), index);
        }
        else Status = "Это поле формы пока не поддерживает заполнение в PaperDesk";
        return true;
    }

    /// <summary>Follows a link: pages directly, external addresses only after the user confirms the target.</summary>
    public void OpenLink(PdfLink link)
    {
        if (link.PageIndex >= 0)
        {
            CurrentPage = link.PageIndex + 1;
            return;
        }
        if (link.Uri is not { } uri || !Uri.TryCreate(uri, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https" or "mailto"))
        {
            if (link.Uri is not null) Status = "Ссылки такого типа не открываются из соображений безопасности";
            return;
        }
        var host = target.Scheme == "mailto" ? target.UserInfo + "@" + target.IdnHost : target.IdnHost;
        if (!_dialogs.Confirm("Открыть внешнюю ссылку", $"Документ ссылается на внешний адрес:\n\n{target.AbsoluteUri}\n\nСайт: {host}\n\nОткрыть его?")) return;
        Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
    }

    public async Task DeleteCommentAsync(PdfComment comment)
    {
        if (!CanInteract) return;
        // Indexes after the removed annotation shift, so the selection must not be re-resolved by index.
        if (SelectedAnnotation?.PageIndex == comment.PageIndex) SelectedAnnotation = null;
        await ChangeAsync(d => d.DeleteComment(comment), comment.PageIndex);
        Status = "Аннотация удалена • Ctrl+Z — отменить";
    }

    private void SetMatches(IReadOnlyList<SearchMatch> matches)
    {
        Matches.Clear();
        foreach (var match in matches) Matches.Add(match);
        _pageMatches = matches.GroupBy(m => m.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
        _matchesVersion++;
        Notify(nameof(MatchesVersion));
    }

    private void CancelSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        IsSearching = false;
    }

    private async void QueueSearch()
    {
        CancelSearch();
        SetMatches([]);
        SelectedMatch = null;
        Notify(nameof(SearchSummary));
        if (!HasDocument || string.IsNullOrWhiteSpace(Query)) return;
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var token = cancellation.Token;
        var document = Document!;
        var query = Query;
        var matchCase = MatchCase;
        var wholeWord = WholeWord;
        IsSearching = true;
        try
        {
            await Task.Delay(350, token);
            var results = await Task.Run(() => document.Search(query, matchCase, wholeWord, token), token);
            if (token.IsCancellationRequested || Document != document) return;
            SetMatches(results);
            SelectedMatch = Matches.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) ShowError(ex);
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                IsSearching = false;
                Notify(nameof(SearchSummary));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private void MoveMatch(int delta)
    {
        if (Matches.Count == 0) return;
        SelectedMatch = Matches[(Matches.IndexOf(SelectedMatch!) + delta + Matches.Count) % Matches.Count];
    }

    public void SetViewportWidth(double width)
    {
        _viewportWidth = Math.Max(300, width);
        if (_fitWidth) FitWidth();
    }

    private void FitWidth()
    {
        if (Pages.Count > 0) Zoom = Math.Max(200, _viewportWidth - 76) / (Pages.Max(p => p.Info.Width) * 96 / 72);
    }

    private void ShowProperties()
    {
        if (Info is not { } info) return;
        var signatures = info.Signatures == 0 ? "0" : $"{info.Signatures} (сохранение инкрементальное, подписанная версия не изменяется)";
        _dialogs.Info("Свойства документа",
            $"Файл: {ActiveTab!.Path ?? "Новый документ"}\n\n" +
            $"Заголовок: {info.Title}\nАвтор: {info.Author}\nТема: {info.Subject}\nСоздано в: {info.Creator}\n" +
            $"Страниц: {info.Pages}\nЦифровых подписей: {signatures}\nФормы: {(info.FormType == 0 ? "Нет" : "Есть")}\n\n" +
            $"Печать: {(info.CanPrint ? "Разрешена" : "Запрещена")}\n" +
            $"Редактирование: {(info.CanEdit ? "Разрешено" : "Запрещено")}\n" +
            $"Копирование текста: {(info.CanCopy ? "Разрешено" : "Запрещено")}");
    }

    // Crash recovery -----------------------------------------------------------------------------

    /// <summary>Writes copies of modified documents that changed since the previous autosave.</summary>
    public async Task AutosaveAsync()
    {
        if (_autosaving) return;
        _autosaving = true;
        try
        {
            foreach (var tab in Documents.ToArray())
            {
                var document = tab.Document;
                if (!document.IsDirty)
                {
                    if (tab.AutosavedRevision >= 0) DeleteRecovery(tab);
                    continue;
                }
                if (tab.AutosavedRevision == document.Revision) continue;
                try
                {
                    var snapshot = await Task.Run(document.GetRecoverySnapshot);
                    await Task.Run(() => _recovery.Write(tab.RecoveryId, snapshot));
                    tab.AutosavedRevision = snapshot.Revision;
                }
                catch (ObjectDisposedException)
                {
                    // The tab was closed while its copy was being written.
                }
                // A tab closed or saved meanwhile must not leave a stale copy behind.
                if (!Documents.Contains(tab) || !document.IsDirty) DeleteRecovery(tab);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Не удалось записать резервную копию", ex);
            Status = "Не удалось сохранить резервную копию: " + ex.Message;
        }
        finally
        {
            _autosaving = false;
        }
    }

    /// <summary>Last-chance copies for crash handlers; never waits long for a busy document.</summary>
    public void EmergencySave()
    {
        DocumentTab[] tabs;
        try
        {
            tabs = Documents.ToArray();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        foreach (var tab in tabs)
        {
            try
            {
                if (!tab.Document.IsDirty || tab.AutosavedRevision == tab.Document.Revision) continue;
                if (tab.Document.TryGetRecoverySnapshot(TimeSpan.FromSeconds(2)) is { } snapshot) _recovery.Write(tab.RecoveryId, snapshot);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Аварийное сохранение не удалось", ex);
            }
        }
    }

    /// <summary>Offers documents autosaved by a PaperDesk process that ended unexpectedly.</summary>
    public async Task RestoreRecoveredAsync()
    {
        var entries = await Task.Run(_recovery.FindOrphaned);
        if (entries.Count == 0) return;
        var names = string.Join("\n", entries.Take(8).Select(e => $"• {e.Name} ({e.SavedAt:g})")) + (entries.Count > 8 ? "\n…" : "");
        if (!_dialogs.Confirm("Восстановление документов",
                $"PaperDesk был закрыт непредвиденно. Найдены несохранённые изменения:\n\n{names}\n\nВосстановить их? При отказе резервные копии будут удалены."))
        {
            foreach (var entry in entries) _recovery.Delete(entry.Id);
            return;
        }
        foreach (var entry in entries)
        {
            try
            {
                var bytes = await Task.Run(() => _recovery.Read(entry));
                PdfDocument? document = null;
                await BusyAsync("Восстановление документа…", async () =>
                {
                    document = await LoadWithPasswordAsync(entry.Name,
                        password => PdfDocument.Restore(bytes, password, entry.OriginalPath, RecoveryService.ParseHash(entry.DiskHash)));
                    if (document is not null) await AddDocumentAsync(document);
                });
                // A cancelled password prompt keeps the copy for the next start.
                if (document is not null) _recovery.Delete(entry.Id);
            }
            catch (InvalidDataException ex)
            {
                _recovery.Delete(entry.Id);
                ShowError(new InvalidDataException($"Резервная копия «{entry.Name}» повреждена и удалена.", ex));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowError(ex);
            }
        }
        if (HasDocument) Status = "Документы восстановлены. Сохраните их, чтобы не потерять изменения.";
    }

    private void DeleteRecovery(DocumentTab tab)
    {
        _recovery.Delete(tab.RecoveryId);
        tab.AutosavedRevision = -1;
    }

    public void Dispose()
    {
        CancelSearch();
        foreach (var tab in Documents)
        {
            if (tab.Path is { } path) _settings.Touch(path, tab.LastPage);
            tab.Dispose();
            // The user already decided about unsaved changes in PrepareCloseAsync.
            DeleteRecovery(tab);
        }
        _settings.TrySave();
    }
}
