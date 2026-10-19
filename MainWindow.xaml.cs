using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Kancelyariya.Models;
using Kancelyariya.Services;
using Kancelyariya.Views;

namespace Kancelyariya;

public partial class MainWindow : Window
{
    private readonly DatabaseStore _store;
    private bool _isInitialized = false;
    private static readonly Random _rnd = new();

    private static readonly string[] OfficeWisdoms =
    [
        "Завхоз: Синяя ручка — расходник, ножницы — священный артефакт!",
        "Завхоз: По статистике, 85% степлеров заряжены надеждой, а не скобами.",
        "Завхоз: Бумага стерпит всё, кроме замятия на 49-й странице из 50.",
        "Завхоз: Если приклеить стикер на монитор, задача решится сама (проверено).",
        "Завхоз: Закон офиса: красная ручка живет вечно, синяя исчезает первой.",
        "Завхоз: Тот, кто берет чужую кружку, рано или поздно перепутает корректор с молоком.",
        "Завхоз: Антистеплер — единственный хищник в естественной офисной среде.",
        "Завхоз: Карандаш всегда ломается именно тогда, когда мысль гениальна.",
        "Завхоз: Секрет продуктивности: купить идеальный блокнот и бояться его испортить.",
        "Завхоз: Запас скрепок в офисе прямо пропорционален спокойствию бухгалтерии!"
    ];

    private readonly System.Windows.Threading.DispatcherTimer _productSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly System.Windows.Threading.DispatcherTimer _journalSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public MainWindow()
    {
        InitializeComponent();

        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string dbPath = Path.Combine(appDir, "data", "kancelyariya.db");
        string backupDir = Path.Combine(appDir, "backups");

        _store = new DatabaseStore(dbPath, backupDir);

        // Если в базе мало данных, автоматически дополняем базовым набором
        if (_store.GetProducts().Count < 4)
        {
            _store.SeedDemo(force: true);
        }

        // Автокопия при старте (не чаще раза в день)
        _store.AutoBackup(daily: true);

        TxtStatusDbPath.Text = $"База данных: {_store.DbPath}";

        _productSearchTimer.Tick += (_, _) => { _productSearchTimer.Stop(); RefreshProducts(); };
        _journalSearchTimer.Tick += (_, _) => { _journalSearchTimer.Stop(); RefreshJournal(); };

        InitJournalFilters();
        RefreshAll();

        _isInitialized = true;

        Closing += OnWindowClosing;
    }

    private void InitJournalFilters()
    {
        CmbJournalType.ItemsSource = new List<string> { "Все операции", "Приход", "Выдача", "Списание" };
        CmbJournalType.SelectedIndex = 0;
    }

    private void RefreshAll()
    {
        RefreshProducts();
        RefreshJournal();
        RefreshEmployees();
        RefreshDeficit();
        UpdateDeficitBadge();
    }

    #region Вкладка: Товары

    private void RefreshProducts()
    {
        string query = TxtProductSearch?.Text ?? "";
        bool lowOnly = ChkLowOnly?.IsChecked == true;

        var products = _store.GetProducts(query, lowOnly);
        GridProducts.ItemsSource = products;

        var (totalCount, lowCount) = _store.GetProductsCountSummary();
        TxtProductsSummary.Text = $"Позиций: {totalCount}.  Требуют заказа: {lowCount}";
    }

    private void OnAddProductClick(object sender, RoutedEventArgs e)
    {
        var dlg = new ProductDialog(_store) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            RefreshAll();
        }
    }

    private void OnEditProductClick(object sender, RoutedEventArgs e)
    {
        if (GridProducts.SelectedItem is Product selected)
        {
            var dlg = new ProductDialog(_store, selected.Id) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                RefreshAll();
            }
        }
        else
        {
            MessageBox.Show("Выберите товар для изменения.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnGridProductsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridProducts.SelectedItem is Product)
        {
            OnEditProductClick(sender, e);
        }
    }

    private void OnOpInClick(object sender, RoutedEventArgs e) => OpenOperationDialog("in");
    private void OnOpOutClick(object sender, RoutedEventArgs e) => OpenOperationDialog("out");
    private void OnOpWriteoffClick(object sender, RoutedEventArgs e) => OpenOperationDialog("writeoff");

    private void OpenOperationDialog(string kind, int? preselectedId = null)
    {
        var products = _store.GetProducts();
        if (products.Count == 0)
        {
            MessageBox.Show("Сначала добавьте товар.", "Нет товаров", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int? selectedPid = preselectedId ?? (GridProducts.SelectedItem as Product)?.Id;
        var dlg = new OperationDialog(_store, kind, selectedPid) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            RefreshAll();
        }
    }

    private void OnProductFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) RefreshProducts();
    }

    private void OnProductSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized) return;
        _productSearchTimer.Stop();
        _productSearchTimer.Start();
    }

    #endregion

    #region Вкладка: Журнал операций

    private void RefreshJournal()
    {
        string selectedRecipient = CmbJournalRecipient.SelectedItem as string ?? "Все получатели";
        var recipients = new List<string> { "Все получатели" };
        recipients.AddRange(_store.GetRecipients());
        CmbJournalRecipient.ItemsSource = recipients;

        if (recipients.Contains(selectedRecipient))
        {
            CmbJournalRecipient.SelectedItem = selectedRecipient;
        }
        else
        {
            CmbJournalRecipient.SelectedIndex = 0;
        }

        string typeTitle = CmbJournalType.SelectedItem as string ?? "Все операции";
        string kind = DatabaseStore.Types.FirstOrDefault(x => x.Value == typeTitle).Key ?? "";

        string recipient = (CmbJournalRecipient.SelectedItem as string) ?? "";
        if (recipient == "Все получатели") recipient = "";

        string query = TxtJournalSearch?.Text ?? "";

        var journal = _store.GetJournal(kind, recipient, query);
        GridJournal.ItemsSource = journal;
    }

    private void OnJournalFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialized) RefreshJournal();
    }

    private void OnJournalSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized) return;
        _journalSearchTimer.Stop();
        _journalSearchTimer.Start();
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Filter = "CSV файлы (*.csv)|*.csv|Все файлы (*.*)|*.*",
            FileName = "zhurnal_operaciy.csv",
            DefaultExt = ".csv"
        };

        if (sfd.ShowDialog(this) == true)
        {
            try
            {
                var rows = GridJournal.ItemsSource as IEnumerable<Operation> ?? new List<Operation>();
                _store.ExportToCsv(sfd.FileName, rows);
                MessageBox.Show($"Журнал сохранён:\n{sfd.FileName}", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сохранения CSV:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    #endregion

    #region Вкладка: Сотрудники

    private void RefreshEmployees()
    {
        GridEmployees.ItemsSource = _store.GetEmployees();
    }

    private void OnAddEmployeeClick(object sender, RoutedEventArgs e)
    {
        var dlg = new EmployeeDialog(_store) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            RefreshAll();
        }
    }

    private void OnEditEmployeeClick(object sender, RoutedEventArgs e)
    {
        if (GridEmployees.SelectedItem is Employee selected)
        {
            var dlg = new EmployeeDialog(_store, selected.Id) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                RefreshAll();
            }
        }
        else
        {
            MessageBox.Show("Выберите сотрудника для изменения.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnGridEmployeesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridEmployees.SelectedItem is Employee)
        {
            OnEditEmployeeClick(sender, e);
        }
    }

    private void OnDeleteEmployeeClick(object sender, RoutedEventArgs e)
    {
        if (GridEmployees.SelectedItem is Employee selected)
        {
            var result = MessageBox.Show("Удалить сотрудника? Записи в журнале сохранятся.", "Удаление",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _store.DeleteEmployee(selected.Id);
                RefreshAll();
            }
        }
        else
        {
            MessageBox.Show("Выберите сотрудника для удаления.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    #endregion

    #region Вкладка: Дефицит / Заказ

    private void RefreshDeficit()
    {
        var lowProducts = _store.GetProducts("", lowOnly: true);
        if (GridDeficit != null) GridDeficit.ItemsSource = lowProducts;
        if (TxtDeficitSummary != null)
        {
            TxtDeficitSummary.Text = $"Критический остаток: {lowProducts.Count} позиций. Рекомендуется пополнить запас.";
        }
    }

    private void UpdateDeficitBadge()
    {
        var (_, lowCount) = _store.GetProductsCountSummary();

        if (TxtHeaderBadge != null)
        {
            TxtHeaderBadge.Text = lowCount > 0 ? $"Внимание: дефицит ({lowCount})" : "Склад в норме";
        }

        if (BadgeDeficit != null && TxtDeficitBadge != null)
        {
            if (lowCount > 0)
            {
                TxtDeficitBadge.Text = lowCount > 99 ? "99+" : lowCount.ToString();
                BadgeDeficit.Visibility = Visibility.Visible;
            }
            else
            {
                BadgeDeficit.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnDeficitOpInClick(object sender, RoutedEventArgs e)
    {
        int? pid = (GridDeficit.SelectedItem as Product)?.Id;
        OpenOperationDialog("in", pid);
    }

    private void OnGridDeficitDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GridDeficit.SelectedItem is Product selected)
        {
            OpenOperationDialog("in", selected.Id);
        }
    }

    private void OnExportOrderCsvClick(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Filter = "CSV файлы (*.csv)|*.csv|Все файлы (*.*)|*.*",
            FileName = "zayavka_kancelyarii.csv",
            DefaultExt = ".csv"
        };

        if (sfd.ShowDialog(this) == true)
        {
            try
            {
                var rows = GridDeficit.ItemsSource as IEnumerable<Product> ?? new List<Product>();
                _store.ExportOrderCsv(sfd.FileName, rows);
                MessageBox.Show($"Ведомость заказа сохранена:\n{sfd.FileName}", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сохранения:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    #endregion

    #region Навигация через Toolbar

    private void OnNavProductsClick(object sender, RoutedEventArgs e)
    {
        if (MainTabs != null) MainTabs.SelectedIndex = 0;
    }

    private void OnNavJournalClick(object sender, RoutedEventArgs e)
    {
        if (MainTabs != null) MainTabs.SelectedIndex = 1;
    }

    private void OnNavEmployeesClick(object sender, RoutedEventArgs e)
    {
        if (MainTabs != null) MainTabs.SelectedIndex = 2;
    }

    private void OnNavDeficitClick(object sender, RoutedEventArgs e)
    {
        if (MainTabs != null) MainTabs.SelectedIndex = 3;
    }

    private void OnMainTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs) return;
        switch (MainTabs.SelectedIndex)
        {
            case 0:
                if (RbNavProducts != null) RbNavProducts.IsChecked = true;
                break;
            case 1:
                if (RbNavJournal != null) RbNavJournal.IsChecked = true;
                break;
            case 2:
                if (RbNavEmployees != null) RbNavEmployees.IsChecked = true;
                break;
            case 3:
                if (RbNavDeficit != null) RbNavDeficit.IsChecked = true;
                break;
        }
    }

    private void OnQuickActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void OnToggleLowFilterClick(object sender, RoutedEventArgs e)
    {
        if (MainTabs != null) MainTabs.SelectedIndex = 0;
        if (ChkLowOnly != null)
        {
            ChkLowOnly.IsChecked = !(ChkLowOnly.IsChecked == true);
        }
    }

    #endregion

    #region Меню Файл

    private void OnMenuManualBackupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = _store.Backup("_manual");
            MessageBox.Show($"Копия сохранена:\n{path}", "Резервная копия", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка создания копии:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnMenuRestoreClick(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Выберите резервную копию",
            InitialDirectory = _store.BackupDir,
            Filter = "База данных (*.db)|*.db|Все файлы (*.*)|*.*"
        };

        if (ofd.ShowDialog(this) == true)
        {
            var confirm = MessageBox.Show(
                "Текущие данные будут заменены данными из копии.\nПеред этим программа сохранит текущую базу в папку backups. Продолжить?",
                "Восстановление", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                _store.Restore(ofd.FileName);
                RefreshAll();
                MessageBox.Show("Данные восстановлены.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка восстановления:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnMenuSeedDemoClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _store.SeedDemo(force: true);
            RefreshAll();
            MessageBox.Show("Таблицы успешно дополнены расширенным каталогом канцелярии, сотрудниками и журналом операций!", "Демо-данные", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOfficeWisdomClick(object sender, RoutedEventArgs e)
    {
        if (TxtOfficeWisdom != null)
        {
            string current = TxtOfficeWisdom.Text;
            string next;
            do
            {
                next = OfficeWisdoms[_rnd.Next(OfficeWisdoms.Length)];
            } while (next == current && OfficeWisdoms.Length > 1);

            TxtOfficeWisdom.Text = next;
        }
    }

    private void OnOpenFunStatsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new FunStatsDialog(_store) { Owner = this };
        dlg.ShowDialog();
    }

    private void OnMenuOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        OpenFolderInExplorer(_store.DataDir);
    }

    private void OnMenuOpenBackupsFolderClick(object sender, RoutedEventArgs e)
    {
        OpenFolderInExplorer(_store.BackupDir);
    }

    private void OpenFolderInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            else
            {
                MessageBox.Show($"Папка не найдена:\n{path}", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть папку:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnMenuExitClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _store.Dispose();
    }
}