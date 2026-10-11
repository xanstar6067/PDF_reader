using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using PDF_Reader.Models;
using PDF_Reader.Services;

namespace PDF_Reader.ViewModels;

public sealed class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IUserDialogs _dialogs;
    private readonly SettingsService _settings;
    private DocumentTab? _activeTab;
    private bool _busy, _searching, _matchCase, _wholeWord, _fitWidth;
    private int _currentPage = 1, _navigationTab;
    private double _zoom = 1, _viewportWidth = 900;
    private string _status = "Откройте PDF, чтобы начать работу", _query = "";
    private DocumentTool _tool;
    private SearchMatch? _selectedMatch;
    private CancellationTokenSource? _searchCancellation;
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
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); Notify(nameof(CanInteract)); CommandManager.InvalidateRequerySuggested(); } }
    public bool CanInteract => HasDocument && !IsBusy;
    public bool IsSearching { get => _searching; private set { Set(ref _searching, value); Notify(nameof(SearchSummary)); } }
    public string WindowTitle => (ActiveTab?.Title is { } title ? title + " — " : "") + "PaperDesk PDF";
    public string Status { get => _status; set => Set(ref _status, value); }
    public int PageCount => Pages.Count;
    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            var page = Math.Clamp(value, 1, Math.Max(1, PageCount));
            if (Set(ref _currentPage, page))
            {
                if (ActiveTab is not null) ActiveTab.LastPage = page;
                NavigateRequested?.Invoke(page - 1); CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public void SetVisiblePage(int index)
    {
        if (index < 0 || index >= PageCount) return;
        if (Set(ref _currentPage, index + 1, nameof(CurrentPage)))
        { if (ActiveTab is not null) ActiveTab.LastPage = _currentPage; CommandManager.InvalidateRequerySuggested(); }
    }
    public double Zoom
    {
        get => _zoom;
        set
        {
            var zoom = double.IsFinite(value) ? Math.Clamp(value, .25, 4) : 1;
            if (!Set(ref _zoom, zoom)) return;
            foreach (var page in Pages) page.Zoom = zoom;
            Notify(nameof(ZoomPercent)); _settings.Value.Zoom = zoom;
            if (HasDocument) NavigateRequested?.Invoke(CurrentPage - 1);
        }
    }
    public int ZoomPercent { get => (int)Math.Round(Zoom * 100); set { _fitWidth = false; Zoom = value / 100.0; } }
    public DocumentTool Tool { get => _tool; set { if (Set(ref _tool, value)) { Notify(nameof(ToolHint)); Status = ToolHint; } } }
    public string ToolHint => Tool switch
    {
        DocumentTool.EditText => "Нажмите на текстовый фрагмент, чтобы изменить его",
        DocumentTool.AddText => "Нажмите на страницу, чтобы добавить текст",
        DocumentTool.Highlight => "Протяните мышью по тексту, чтобы выделить его маркером",
        DocumentTool.Note => "Нажмите на страницу, чтобы оставить заметку",
        _ => "Выделяйте текст мышью • Ctrl+C — копировать • Ctrl+колесо — масштаб"
    };
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
            if (value is not null) { CurrentPage = value.PageNumber; NavigateRequested?.Invoke(value.PageIndex); }
        }
    }
    public string SearchSummary => IsSearching ? "Поиск…" : string.IsNullOrWhiteSpace(Query) ? "Введите текст для поиска" :
        Matches.Count == 0 ? "Совпадений нет" : $"{(SelectedMatch is null ? 0 : Matches.IndexOf(SelectedMatch) + 1)} / {Matches.Count}{(Matches.Count == 10000 ? "+" : "")} совпадений";
    public string DocumentSummary => Info is null ? "" : $"{Info.Pages} стр. • {(Info.IsEncrypted ? "Защищённый PDF" : "PDF документ")}" +
        (Info.Signatures > 0 ? " • Есть цифровая подпись: правки могут её нарушить" : "") + (Info.CanEdit ? "" : " • Редактирование запрещено");
    public ICommand OpenCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand PrintCommand { get; }
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
    public ICommand PropertiesCommand { get; }
    public ICommand DefaultAppCommand { get; }

    public WorkspaceViewModel(IUserDialogs dialogs, SettingsService settings)
    {
        _dialogs = dialogs; _settings = settings; _zoom = settings.Value.Zoom;
        foreach (var file in settings.Value.RecentFiles) RecentFiles.Add(file);
        bool Available() => CanInteract;
        bool CanAssemble() => CanInteract && Info?.CanAssemble == true;
        OpenCommand = Async(OpenAsync, () => !IsBusy);
        NewCommand = Async(async () => { await BusyAsync("Создание PDF…", async () => await AddDocumentAsync(await Task.Run(PdfDocument.Create))); }, () => !IsBusy);
        SaveCommand = Async(() => SaveAsync(ActiveTab!, false), Available);
        SaveAsCommand = Async(() => SaveAsync(ActiveTab!, true), Available);
        CloseCommand = Async(async () => { if (ActiveTab is { } tab) await CloseTabAsync(tab); }, Available);
        PrintCommand = new RelayCommand(() => PrintRequested?.Invoke(), () => CanInteract && Info?.CanPrint == true);
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
        RotateCommand = Async(() => ChangeAsync(d => d.RotatePage(CurrentPage - 1, 1)), CanAssemble);
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
        CopyPageTextCommand = Async(async () =>
        {
            var document = Document!; var index = CurrentPage - 1;
            var text = await Task.Run(() => document.ExtractText(index));
            if (string.IsNullOrWhiteSpace(text)) { Status = "На странице нет текстового слоя. Для скана требуется OCR."; return; }
            Clipboard.SetText(text); Status = "Текст страницы скопирован";
        }, () => CanInteract && Info?.CanCopy == true);
        PropertiesCommand = new RelayCommand(ShowProperties, Available);
        DefaultAppCommand = new RelayCommand(() => DefaultAppRequested?.Invoke(), () => !IsBusy);
    }
    private AsyncCommand Async(Func<Task> action, Func<bool>? canExecute = null) => new(action, ShowError, canExecute);
    public void ShowError(Exception error) { Status = error.Message; _dialogs.Error(error); }
    private async Task BusyAsync(string status, Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; Status = status;
        try { await action(); }
        finally { IsBusy = false; }
    }
    private async void ActivateDocumentAsync()
    {
        CancelSearch(); _tool = DocumentTool.Select; Notify(nameof(Tool));
        try { await BusyAsync("Загрузка документа…", RefreshAsync); if (!string.IsNullOrWhiteSpace(Query)) QueueSearch(); }
        catch (Exception ex) { ShowError(ex); }
    }
    private async Task RefreshAsync()
    {
        var document = Document;
        var data = document is null ? null : await Task.Run(() => new WorkspaceData(document.GetInfo(), document.GetPages(), document.GetOutline(), document.GetComments()));
        Pages.Clear(); Outline.Clear(); Comments.Clear(); Matches.Clear(); SelectedMatch = null;
        Info = data?.Info;
        if (document is not null && data is not null)
        {
            foreach (var page in data.Pages) Pages.Add(new(document, page, Zoom));
            foreach (var node in data.Outline) Outline.Add(node);
            foreach (var comment in data.Comments) Comments.Add(comment);
            _currentPage = Math.Clamp(ActiveTab!.LastPage, 1, PageCount);
            ActiveTab.Refresh(); if (_fitWidth) FitWidth();
            Status = ToolHint;
        }
        else { _currentPage = 1; Status = "Откройте PDF, чтобы начать работу"; }
        foreach (var name in new[] { nameof(HasDocument), nameof(CanInteract), nameof(Document), nameof(PageCount), nameof(CurrentPage), nameof(WindowTitle), nameof(Info), nameof(DocumentSummary), nameof(SearchSummary) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
        if (HasDocument) NavigateRequested?.Invoke(CurrentPage - 1);
    }
    private record WorkspaceData(DocumentInfo Info, IReadOnlyList<PdfPageInfo> Pages, IReadOnlyList<OutlineNode> Outline, IReadOnlyList<PdfComment> Comments);
    private async Task AddDocumentAsync(PdfDocument document)
    {
        var tab = new DocumentTab(document);
        if (document.FilePath is { } path)
        {
            tab.LastPage = _settings.Value.RecentFiles.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))?.Page ?? 1;
            _settings.Touch(path, tab.LastPage); ReloadRecents();
        }
        Documents.Add(tab); _activeTab = tab; Notify(nameof(ActiveTab)); await RefreshAsync();
    }
    private void ReloadRecents() { RecentFiles.Clear(); foreach (var file in _settings.Value.RecentFiles) RecentFiles.Add(file); }
    public async Task OpenAsync()
    {
        foreach (var path in _dialogs.ChooseOpenFiles()) await OpenPathAsync(path);
    }
    public async Task OpenPathAsync(string path)
    {
        if (IsBusy) return;
        var existing = Documents.FirstOrDefault(t => string.Equals(t.Path, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing != ActiveTab)
            {
                _activeTab = existing; Notify(nameof(ActiveTab)); CancelSearch(); Tool = DocumentTool.Select;
                await BusyAsync("Загрузка документа…", RefreshAsync);
            }
            return;
        }
        await BusyAsync("Открытие PDF…", async () =>
        {
            var document = await LoadWithPasswordAsync(path);
            if (document is not null) await AddDocumentAsync(document);
        });
    }
    private async Task<PdfDocument?> LoadWithPasswordAsync(string path)
    {
        string? password = null;
        while (true)
        {
            try { return await Task.Run(() => PdfDocument.Open(path, password)); }
            catch (PdfPasswordException)
            {
                password = _dialogs.AskText("Защищённый документ", "Введите пароль для открытия «" + System.IO.Path.GetFileName(path) + "».", password: true);
                if (password is null) return null;
            }
        }
    }
    public async Task<bool> SaveAsync(DocumentTab tab, bool saveAs)
    {
        var path = saveAs || tab.Path is null ? _dialogs.ChooseSaveFile(tab.Path is null ? "Документ.pdf" : System.IO.Path.GetFileName(tab.Path)) : tab.Path;
        if (path is null) return false;
        if (Documents.Any(other => other != tab && string.Equals(other.Path, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Этот файл уже открыт в другой вкладке. Выберите другое имя для сохранения.");
        var saved = false;
        await BusyAsync("Сохранение PDF…", async () =>
        {
            await Task.Run(() => tab.Document.Save(path)); tab.Refresh(); Notify(nameof(WindowTitle));
            _settings.Touch(path, tab.LastPage); ReloadRecents(); Status = "Документ сохранён"; saved = true;
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
        if (tab.Path is { } path) { _settings.Touch(path, tab.LastPage); ReloadRecents(); }
        var wasActive = ReferenceEquals(ActiveTab, tab); var index = Documents.IndexOf(tab);
        if (wasActive) CancelSearch();
        await BusyAsync("Закрытие документа…", async () =>
        {
            // Suppress selection callbacks until the collection and active document agree.
            Documents.Remove(tab); tab.Dispose();
            if (wasActive)
            {
                _activeTab = Documents.Count > 0 ? Documents[Math.Min(index, Documents.Count - 1)] : null;
                Notify(nameof(ActiveTab)); await RefreshAsync();
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
            if (tab.Document.IsDirty)
            {
                var answer = _dialogs.AskSave(tab.Title.TrimEnd(' ', '•'));
                if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.Yes && !await SaveAsync(tab, false)) return false;
            }
        }
        return true;
    }
    public async Task ChangeAsync(Action<PdfDocument> change)
    {
        if (!CanInteract || Document is not { } document) return;
        CancelSearch();
        await BusyAsync("Обновление документа…", async () => { await Task.Run(() => change(document)); await RefreshAsync(); });
        if (!string.IsNullOrWhiteSpace(Query)) QueueSearch();
    }
    private async Task MovePageAsync(int direction)
    {
        var from = CurrentPage - 1; var to = from + direction;
        await ChangeAsync(d => d.MovePage(from, to)); CurrentPage = to + 1;
    }
    private async Task ImportAsync()
    {
        var paths = _dialogs.ChooseOpenFiles();
        if (paths.Length == 0 || Document is not { } document) return;
        CancelSearch(); var index = CurrentPage;
        await BusyAsync("Импорт страниц…", async () =>
        {
            foreach (var path in paths)
            {
                using var source = await LoadWithPasswordAsync(path);
                if (source is null) continue;
                await Task.Run(() => document.ImportPages(source, index)); index += source.PageCount;
            }
            await RefreshAsync();
        });
    }
    private async Task ExportAsync()
    {
        var range = _dialogs.AskText("Извлечь страницы", $"Введите номера страниц от 1 до {PageCount}. Пример: 1-3,5.", CurrentPage.ToString());
        if (string.IsNullOrWhiteSpace(range)) return;
        var path = _dialogs.ChooseSaveFile("Страницы.pdf");
        if (path is null) return;
        if (string.Equals(System.IO.Path.GetFullPath(path), Document!.FilePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Для извлечённых страниц выберите другой файл.");
        await BusyAsync("Извлечение страниц…", () => Task.Run(() => AtomicFile.Write(path, Document.ExportPages(range))));
        Status = "Страницы сохранены в отдельный PDF";
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
            if (hit is null) { Status = "Здесь нет редактируемого текста. Для текста на скане требуется OCR."; return; }
            var value = _dialogs.EditText("Редактировать текст", "Изменяется выбранный текстовый объект PDF. Положение и цвет сохраняются; шрифт заменяется на Segoe UI с поддержкой кириллицы. Пустой текст удаляет фрагмент.", hit.Text, hit.FontSize, false);
            if (value is null) return;
            await ChangeAsync(d => d.ReplaceText(hit, value.Value.Text, value.Value.Size));
        }
        else if (Tool == DocumentTool.AddText)
        {
            var value = _dialogs.EditText("Добавить текст", "Текст будет добавлен в выбранную точку страницы. Поддерживаются несколько строк и кириллица.", "", 12, true);
            if (value is null || string.IsNullOrWhiteSpace(value.Value.Text)) return;
            await ChangeAsync(d => d.AddText(index, point, value.Value.Text, value.Value.Size));
        }
        else if (Tool == DocumentTool.Note)
        {
            var text = _dialogs.AskText("Добавить заметку", "Заметка сохраняется в PDF и доступна в других программах просмотра.", multiline: true);
            if (string.IsNullOrWhiteSpace(text)) return;
            await ChangeAsync(d => d.AddNote(index, point, text)); NavigationTab = 3;
        }
    }
    public async Task<bool> TryFillFormAtAsync(int index, Point point)
    {
        if (!CanInteract || Info?.FormType != 1) return false;
        var document = Document!;
        var field = await Task.Run(() => document.HitFormField(index, point));
        if (Document != document || !CanInteract) return false;
        if (field is null) return false;
        if (field.ReadOnly) { Status = "Это поле формы доступно только для чтения"; return true; }
        if (field.Type is 2 or 3) await ChangeAsync(d => d.FillFormField(field, ""));
        else if (field.Type is 4 or 6)
        {
            var value = _dialogs.AskText("Заполнить форму", "Поле: " + field.Name, field.Value, multiline: true);
            if (value is not null) await ChangeAsync(d => d.FillFormField(field, value));
        }
        else Status = "Это поле формы пока не поддерживает заполнение в PaperDesk";
        return true;
    }
    public async Task DeleteCommentAsync(PdfComment comment)
    {
        if (CanInteract && _dialogs.Confirm("Удалить аннотацию", "Удалить выбранную аннотацию?")) await ChangeAsync(d => d.DeleteComment(comment));
    }
    private void CancelSearch()
    {
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose(); _searchCancellation = null; IsSearching = false;
    }
    private async void QueueSearch()
    {
        CancelSearch(); Matches.Clear(); SelectedMatch = null; Notify(nameof(SearchSummary));
        if (!HasDocument || string.IsNullOrWhiteSpace(Query)) return;
        var cancellation = new CancellationTokenSource(); _searchCancellation = cancellation;
        var token = cancellation.Token; var document = Document!; var query = Query; var matchCase = MatchCase; var wholeWord = WholeWord;
        IsSearching = true;
        try
        {
            await Task.Delay(350, token);
            var results = await Task.Run(() => document.Search(query, matchCase, wholeWord, token), token);
            if (token.IsCancellationRequested || Document != document) return;
            foreach (var match in results) Matches.Add(match);
            SelectedMatch = Matches.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ShowError(ex); }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation)) { IsSearching = false; Notify(nameof(SearchSummary)); CommandManager.InvalidateRequerySuggested(); }
        }
    }
    private void MoveMatch(int delta)
    {
        if (Matches.Count == 0) return;
        SelectedMatch = Matches[(Matches.IndexOf(SelectedMatch!) + delta + Matches.Count) % Matches.Count];
    }
    public void SetViewportWidth(double width) { _viewportWidth = Math.Max(300, width); if (_fitWidth) FitWidth(); }
    private void FitWidth()
    {
        if (Pages.Count > 0) Zoom = Math.Max(200, _viewportWidth - 76) / (Pages.Max(p => p.Info.Width) * 96 / 72);
    }
    private void ShowProperties()
    {
        if (Info is not { } info) return;
        _dialogs.Info("Свойства документа", $"Файл: {ActiveTab!.Path ?? "Новый документ"}\n\nЗаголовок: {info.Title}\nАвтор: {info.Author}\nТема: {info.Subject}\nСоздано в: {info.Creator}\nСтраниц: {info.Pages}\nЦифровых подписей: {info.Signatures}\nФормы: {(info.FormType == 0 ? "Нет" : "Есть")}\n\nПечать: {(info.CanPrint ? "Разрешена" : "Запрещена")}\nРедактирование: {(info.CanEdit ? "Разрешено" : "Запрещено")}\nКопирование текста: {(info.CanCopy ? "Разрешено" : "Запрещено")}");
    }
    public void Dispose()
    {
        CancelSearch();
        foreach (var tab in Documents)
        { if (tab.Path is { } path) _settings.Touch(path, tab.LastPage); tab.Dispose(); }
        try { _settings.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A settings failure must not prevent shutdown. */ }
    }
}
