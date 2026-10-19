using System;
using System.IO;
using System.Linq;
using Xunit;
using Kancelyariya.Services;
using Kancelyariya.Models;

namespace Kancelyariya.Tests;

public class StoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _backupDir;
    private readonly DatabaseStore _store;

    public StoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KancelyariyaTest_" + Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_tempDir, "data", "test.db");
        _backupDir = Path.Combine(_tempDir, "backups");
        _store = new DatabaseStore(_dbPath, _backupDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void TestSeedDemoAndProductListing()
    {
        _store.SeedDemo();
        var prods = _store.GetProducts();
        Assert.True(prods.Count >= 6);

        var emps = _store.GetEmployees();
        Assert.True(emps.Count >= 2);

        var categories = _store.GetCategories();
        Assert.Contains("Бумага", categories);
        Assert.Contains("Письменные", categories);
        Assert.Contains("Офис", categories);

        var lowProds = _store.GetProducts(lowOnly: true);
        Assert.NotEmpty(lowProds);
        Assert.All(lowProds, p => Assert.True(p.IsLow));
    }

    [Fact]
    public void TestProductValidationAndDuplicate()
    {
        _store.SaveProduct(null, "Клей-карандаш", "Канцтовары", "шт.", 5, 10);
        var p = _store.GetProducts("Клей-карандаш").FirstOrDefault();
        Assert.NotNull(p);
        Assert.Equal(10, p.Qty);
        Assert.Equal(5, p.MinQty);

        // Дубликат названия (без учета регистра)
        Assert.Throws<ArgumentException>(() =>
            _store.SaveProduct(null, "клей-карандаш", "Другое", "шт.", 1, 1));

        // Пустое название
        Assert.Throws<ArgumentException>(() =>
            _store.SaveProduct(null, "   ", "Другое", "шт.", 1, 1));

        // Отрицательное количество
        Assert.Throws<ArgumentException>(() =>
            _store.SaveProduct(null, "Ластик", "Канцтовары", "шт.", -1, 0));
    }

    [Fact]
    public void TestEmployeeCrud()
    {
        _store.SaveEmployee(null, "Петров Петр Петрович", "ИТ-отдел");
        var emps = _store.GetEmployees();
        var emp = emps.FirstOrDefault(e => e.Name == "Петров Петр Петрович");
        Assert.NotNull(emp);
        Assert.Equal("ИТ-отдел", emp.Dept);

        // Редактирование
        _store.SaveEmployee(emp.Id, "Петров Петр Сидорович", "ИТ-отдел и безопасность");
        var updated = _store.GetEmployees().First(e => e.Id == emp.Id);
        Assert.Equal("Петров Петр Сидорович", updated.Name);
        Assert.Equal("ИТ-отдел и безопасность", updated.Dept);

        // Удаление
        _store.DeleteEmployee(emp.Id);
        Assert.DoesNotContain(_store.GetEmployees(), e => e.Id == emp.Id);
    }

    [Fact]
    public void TestOperations()
    {
        _store.SaveProduct(null, "Блокнот А5", "Бумага", "шт.", 5, 20);
        var prod = _store.GetProducts("Блокнот А5").First();
        _store.SaveEmployee(null, "Сидорова Елена", "Кадры");
        var emp = _store.GetEmployees().First();

        // 1. Приход
        _store.Operate("in", prod.Id, 15, null, "Поставка №123");
        var afterIn = _store.GetProduct(prod.Id);
        Assert.NotNull(afterIn);
        Assert.Equal(35, afterIn.Qty); // 20 + 15

        // 2. Выдача
        _store.Operate("out", prod.Id, 10, emp.Id, "Для совещания");
        var afterOut = _store.GetProduct(prod.Id);
        Assert.NotNull(afterOut);
        Assert.Equal(25, afterOut.Qty); // 35 - 10

        // 3. Выдача сверх остатка - должна быть ошибка
        Assert.Throws<ArgumentException>(() =>
            _store.Operate("out", prod.Id, 100, emp.Id, "Слишком много"));

        // 4. Списание без причины - ошибка
        Assert.Throws<ArgumentException>(() =>
            _store.Operate("writeoff", prod.Id, 2, null, ""));

        // 5. Корректное списание
        _store.Operate("writeoff", prod.Id, 5, null, "Брак упаковки");
        var afterWriteoff = _store.GetProduct(prod.Id);
        Assert.NotNull(afterWriteoff);
        Assert.Equal(20, afterWriteoff.Qty); // 25 - 5

        // Проверка журнала
        var journal = _store.GetJournal();
        Assert.True(journal.Count >= 4); // начальный остаток + 3 операции
        Assert.Contains(journal, j => j.Type == "in" && j.Note == "Поставка №123");
        Assert.Contains(journal, j => j.Type == "out" && j.Recipient.Contains("Сидорова Елена"));
        Assert.Contains(journal, j => j.Type == "writeoff" && j.Note == "Брак упаковки");
    }

    [Fact]
    public void TestBackupAndRestore()
    {
        _store.SaveProduct(null, "Папка-регистратор", "Офис", "шт.", 2, 5);
        var backupPath = _store.Backup("_test");
        Assert.True(File.Exists(backupPath));

        // Добавляем еще товар
        _store.SaveProduct(null, "Маркер перманентный", "Письменные", "шт.", 1, 10);
        Assert.Equal(2, _store.GetProducts().Count);

        // Восстанавливаем из копии
        _store.Restore(backupPath);

        // После восстановления должен остаться только 1 исходный товар
        var restoredProds = _store.GetProducts();
        Assert.Single(restoredProds);
        Assert.Equal("Папка-регистратор", restoredProds[0].Name);
    }

    [Fact]
    public void TestCsvExport()
    {
        _store.SaveProduct(null, "Флешка 32ГБ", "Электроника", "шт.", 1, 3);
        var prod = _store.GetProducts("Флешка 32ГБ").First();
        _store.Operate("in", prod.Id, 5, null, "Счет-фактура №45; партия 1");

        var journal = _store.GetJournal();
        var csvPath = Path.Combine(_tempDir, "test_export.csv");
        _store.ExportToCsv(csvPath, journal);

        Assert.True(File.Exists(csvPath));
        var lines = File.ReadAllLines(csvPath);
        Assert.True(lines.Length >= 2);
        Assert.Equal("ID;Дата;Товар;Операция;Количество;Ед.;Получатель;Примечание", lines[0]);
        Assert.Contains(lines, l => l.Contains("Флешка 32ГБ") && l.Contains("Приход"));
    }

    [Fact]
    public void PopulateUserDatabase()
    {
        string realDb = @"C:\Users\kazak\OneDrive\Рабочий стол\Kancelyariya_CSharp\data\kancelyariya.db";
        string realBackup = @"C:\Users\kazak\OneDrive\Рабочий стол\Kancelyariya_CSharp\backups";
        using var store = new DatabaseStore(realDb, realBackup);
        store.ResetDatabase();
        store.SeedDemo(force: true);
        var prods = store.GetProducts();
        Assert.Equal(10, prods.Count);
    }

    [Fact]
    public void TestHighVolumeDataPerformance()
    {
        // Стресс-тест на 2000 товаров и операций
        for (int i = 0; i < 2000; i++)
        {
            _store.SaveProduct(null, $"Товар стресс-теста #{i}", "Тестовая категория", "шт.", 5, 10);
        }

        var summary = _store.GetProductsCountSummary();
        Assert.True(summary.totalCount >= 2000);

        var searchResults = _store.GetProducts("теста #15");
        Assert.NotEmpty(searchResults);

        var journal = _store.GetJournal(query: "стресс");
        Assert.NotEmpty(journal);
    }
}
