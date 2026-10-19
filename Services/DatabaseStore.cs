using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Kancelyariya.Models;

namespace Kancelyariya.Services;

public class DatabaseStore : IDisposable
{
    private readonly string _dbPath;
    private readonly string _backupDir;
    private SqliteConnection _connection = null!;
    private bool _isDirty = false;
    private const int KeepAutoBackups = 30;

    public static readonly string[] Units = ["шт.", "уп.", "пачка", "рулон", "компл."];
    public static readonly Dictionary<string, string> Types = new()
    {
        { "in", "Приход" },
        { "out", "Выдача" },
        { "writeoff", "Списание" }
    };

    public string DbPath => _dbPath;
    public string BackupDir => _backupDir;
    public string DataDir => Path.GetDirectoryName(_dbPath) ?? string.Empty;
    public bool Dirty => _isDirty;

    public DatabaseStore(string dbPath, string backupDir)
    {
        _dbPath = Path.GetFullPath(dbPath);
        _backupDir = Path.GetFullPath(backupDir);

        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (!Directory.Exists(_backupDir))
        {
            Directory.CreateDirectory(_backupDir);
        }

        OpenConnection();
        InitializeSchema();
    }

    private void OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();

        _connection.CreateCollation("NOCASE_RU", (s1, s2) => string.Compare(s1, s2, StringComparison.CurrentCultureIgnoreCase));
        _connection.CreateFunction("ru_contains", (string? text, string? pattern) => 
            !string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(pattern) && text.Contains(pattern, StringComparison.CurrentCultureIgnoreCase));

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            cmd.ExecuteNonQuery();
        }
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS products(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL UNIQUE,
            category TEXT NOT NULL DEFAULT '',
            unit TEXT NOT NULL DEFAULT 'шт.',
            qty INTEGER NOT NULL DEFAULT 0 CHECK(qty >= 0),
            min_qty INTEGER NOT NULL DEFAULT 0 CHECK(min_qty >= 0)
        );
        CREATE TABLE IF NOT EXISTS employees(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            dept TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE IF NOT EXISTS ops(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            product_id INTEGER,
            product_name TEXT NOT NULL,
            unit TEXT NOT NULL,
            type TEXT NOT NULL,
            qty INTEGER NOT NULL,
            recipient TEXT NOT NULL DEFAULT '',
            note TEXT NOT NULL DEFAULT '',
            date TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_ops_product ON ops(product_id);
        CREATE INDEX IF NOT EXISTS ix_ops_date ON ops(date);
        CREATE INDEX IF NOT EXISTS ix_ops_type ON ops(type);
        CREATE INDEX IF NOT EXISTS ix_ops_recipient ON ops(recipient);
        CREATE INDEX IF NOT EXISTS ix_products_name ON products(name);
        CREATE INDEX IF NOT EXISTS ix_products_category ON products(category);
        PRAGMA user_version = 1;
        """;
        cmd.ExecuteNonQuery();
    }

    #region Резервные копии

    public string Backup(string tag = "")
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var fileName = $"kancelyariya_{timestamp}{tag}.db";
        var destPath = Path.Combine(_backupDir, fileName);

        using (var destConn = new SqliteConnection($"Data Source={destPath}"))
        {
            destConn.Open();
            _connection.BackupDatabase(destConn);
        }

        _isDirty = false;

        if (tag == "_auto")
        {
            RotateBackups();
        }

        return destPath;
    }

    public string? AutoBackup(bool onlyIfDirty = false, bool daily = false)
    {
        if (onlyIfDirty && !_isDirty)
        {
            return null;
        }

        if (daily)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var existingDaily = Directory.GetFiles(_backupDir, $"kancelyariya_{today}_*_auto.db");
            if (existingDaily.Length > 0)
            {
                return null;
            }
        }

        return Backup("_auto");
    }

    private void RotateBackups()
    {
        try
        {
            var files = Directory.GetFiles(_backupDir, "kancelyariya_*_auto.db")
                .OrderBy(f => f)
                .ToList();

            if (files.Count > KeepAutoBackups)
            {
                var toDelete = files.Take(files.Count - KeepAutoBackups);
                foreach (var file in toDelete)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    public void Restore(string srcPath)
    {
        if (!File.Exists(srcPath))
        {
            throw new FileNotFoundException("Файл резервной копии не найден.", srcPath);
        }

        // Проверяем структуру резервной копии
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var checkConn = new SqliteConnection($"Data Source={srcPath};Mode=ReadOnly"))
            {
                checkConn.Open();
                using var cmd = checkConn.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Этот файл не является корректной базой данных: {ex.Message}");
        }

        var required = new[] { "products", "employees", "ops" };
        if (!required.All(r => tables.Contains(r)))
        {
            throw new InvalidOperationException("Файл не похож на резервную копию этой программы.");
        }

        // Создаем страховочную копию перед восстановлением
        Backup("_before_restore");

        // Восстанавливаем данные через SQLite Backup API
        using (var srcConn = new SqliteConnection($"Data Source={srcPath};Mode=ReadOnly"))
        {
            srcConn.Open();
            srcConn.BackupDatabase(_connection);
        }

        _isDirty = false;
    }

    #endregion

    #region Товары

    public List<Product> GetProducts(string query = "", bool lowOnly = false, int limit = 0)
    {
        var list = new List<Product>();
        var q = query.Trim();

        var sb = new StringBuilder("SELECT id, name, category, unit, qty, min_qty FROM products WHERE 1=1 ");
        if (!string.IsNullOrEmpty(q))
        {
            sb.Append("AND ru_contains(name, @q) ");
        }
        if (lowOnly)
        {
            sb.Append("AND qty <= min_qty ");
        }
        sb.Append("ORDER BY name ");
        if (limit > 0)
        {
            sb.Append($"LIMIT {limit} ");
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sb.ToString();
        if (!string.IsNullOrEmpty(q))
        {
            cmd.Parameters.AddWithValue("@q", q);
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Product
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Category = reader.GetString(2),
                Unit = reader.GetString(3),
                Qty = reader.GetInt32(4),
                MinQty = reader.GetInt32(5)
            });
        }

        return list;
    }

    public (int totalCount, int lowCount) GetProductsCountSummary()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT count(*), count(CASE WHEN qty <= min_qty THEN 1 END) FROM products;";
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return (reader.GetInt32(0), reader.GetInt32(1));
        }
        return (0, 0);
    }

    public Product? GetProduct(int id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, category, unit, qty, min_qty FROM products WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new Product
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Category = reader.GetString(2),
                Unit = reader.GetString(3),
                Qty = reader.GetInt32(4),
                MinQty = reader.GetInt32(5)
            };
        }
        return null;
    }

    public List<string> GetCategories()
    {
        var list = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT category FROM products WHERE category <> '' ORDER BY category;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    public void SaveProduct(int? id, string name, string category, string unit, int minQty, int initialQty = 0)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Укажите название товара.");
        }

        if (minQty < 0 || initialQty < 0)
        {
            throw new ArgumentException("Количество и минимальный остаток не могут быть отрицательными.");
        }

        // Быстрая проверка уникальности названия в SQL
        using (var checkCmd = _connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT id FROM products WHERE name = @name COLLATE NOCASE_RU AND (@id IS NULL OR id <> @id) LIMIT 1;";
            checkCmd.Parameters.AddWithValue("@name", name);
            checkCmd.Parameters.AddWithValue("@id", id.HasValue ? id.Value : DBNull.Value);
            var dup = checkCmd.ExecuteScalar();
            if (dup != null)
            {
                throw new ArgumentException("Товар с таким названием уже есть.");
            }
        }

        using var tx = _connection.BeginTransaction();
        try
        {
            if (id.HasValue)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                UPDATE products 
                SET name = @name, category = @category, unit = @unit, min_qty = @min_qty 
                WHERE id = @id;
                """;
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@category", category.Trim());
                cmd.Parameters.AddWithValue("@unit", unit);
                cmd.Parameters.AddWithValue("@min_qty", minQty);
                cmd.Parameters.AddWithValue("@id", id.Value);
                cmd.ExecuteNonQuery();
            }
            else
            {
                long newId;
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                    INSERT INTO products(name, category, unit, qty, min_qty) 
                    VALUES(@name, @category, @unit, @qty, @min_qty);
                    SELECT last_insert_rowid();
                    """;
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@category", category.Trim());
                    cmd.Parameters.AddWithValue("@unit", unit);
                    cmd.Parameters.AddWithValue("@qty", initialQty);
                    cmd.Parameters.AddWithValue("@min_qty", minQty);
                    newId = (long)(cmd.ExecuteScalar() ?? 0L);
                }

                if (initialQty > 0)
                {
                    using var logCmd = _connection.CreateCommand();
                    logCmd.Transaction = tx;
                    logCmd.CommandText = """
                    INSERT INTO ops(product_id, product_name, unit, type, qty, recipient, note, date)
                    VALUES(@pid, @name, @unit, 'in', @qty, '', 'Начальный остаток', @date);
                    """;
                    logCmd.Parameters.AddWithValue("@pid", newId);
                    logCmd.Parameters.AddWithValue("@name", name);
                    logCmd.Parameters.AddWithValue("@unit", unit);
                    logCmd.Parameters.AddWithValue("@qty", initialQty);
                    logCmd.Parameters.AddWithValue("@date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    logCmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
            _isDirty = true;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    #endregion

    #region Сотрудники

    public List<Employee> GetEmployees()
    {
        var list = new List<Employee>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, dept FROM employees ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Employee
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Dept = reader.GetString(2)
            });
        }
        return list;
    }

    public void SaveEmployee(int? id, string name, string dept)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Укажите ФИО сотрудника.");
        }

        using var cmd = _connection.CreateCommand();
        if (id.HasValue)
        {
            cmd.CommandText = "UPDATE employees SET name = @name, dept = @dept WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id.Value);
        }
        else
        {
            cmd.CommandText = "INSERT INTO employees(name, dept) VALUES(@name, @dept);";
        }
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@dept", dept.Trim());
        cmd.ExecuteNonQuery();
        _isDirty = true;
    }

    public void DeleteEmployee(int id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM employees WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        _isDirty = true;
    }

    #endregion

    #region Операции

    public Product Operate(string kind, int productId, int qty, int? employeeId = null, string note = "")
    {
        note = note.Trim();
        var p = GetProduct(productId);
        if (p == null)
        {
            throw new ArgumentException("Выберите товар.");
        }

        if (qty < 1)
        {
            throw new ArgumentException("Количество должно быть целым числом больше нуля.");
        }

        string recipient = "";
        if (kind == "out")
        {
            if (!employeeId.HasValue)
            {
                throw new ArgumentException("Выберите сотрудника (при необходимости добавьте его на вкладке «Сотрудники»).");
            }

            using var empCmd = _connection.CreateCommand();
            empCmd.CommandText = "SELECT id, name, dept FROM employees WHERE id = @id;";
            empCmd.Parameters.AddWithValue("@id", employeeId.Value);
            using var empReader = empCmd.ExecuteReader();
            if (empReader.Read())
            {
                string eName = empReader.GetString(1);
                string eDept = empReader.GetString(2);
                recipient = string.IsNullOrWhiteSpace(eDept) ? eName : $"{eName} ({eDept})";
            }
            else
            {
                throw new ArgumentException("Выберите сотрудника (при необходимости добавьте его на вкладке «Сотрудники»).");
            }
        }

        if (kind == "writeoff" && string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("Укажите причину списания.");
        }

        if ((kind == "out" || kind == "writeoff") && qty > p.Qty)
        {
            throw new ArgumentException($"Недостаточно товара: на складе {p.Qty} {p.Unit}, запрошено {qty}. Операция не проведена.");
        }

        int delta = kind == "in" ? qty : -qty;

        using var tx = _connection.BeginTransaction();
        try
        {
            using (var updCmd = _connection.CreateCommand())
            {
                updCmd.Transaction = tx;
                updCmd.CommandText = "UPDATE products SET qty = qty + @delta WHERE id = @id;";
                updCmd.Parameters.AddWithValue("@delta", delta);
                updCmd.Parameters.AddWithValue("@id", productId);
                updCmd.ExecuteNonQuery();
            }

            using (var logCmd = _connection.CreateCommand())
            {
                logCmd.Transaction = tx;
                logCmd.CommandText = """
                INSERT INTO ops(product_id, product_name, unit, type, qty, recipient, note, date)
                VALUES(@pid, @name, @unit, @type, @qty, @rec, @note, @date);
                """;
                logCmd.Parameters.AddWithValue("@pid", productId);
                logCmd.Parameters.AddWithValue("@name", p.Name);
                logCmd.Parameters.AddWithValue("@unit", p.Unit);
                logCmd.Parameters.AddWithValue("@type", kind);
                logCmd.Parameters.AddWithValue("@qty", qty);
                logCmd.Parameters.AddWithValue("@rec", recipient);
                logCmd.Parameters.AddWithValue("@note", note);
                logCmd.Parameters.AddWithValue("@date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                logCmd.ExecuteNonQuery();
            }

            tx.Commit();
            _isDirty = true;
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return p;
    }

    public List<Operation> GetJournal(string kind = "", string recipient = "", string query = "", int limit = 2000)
    {
        var list = new List<Operation>();
        var q = query.Trim();

        var sb = new StringBuilder("SELECT id, product_id, product_name, unit, type, qty, recipient, note, date FROM ops WHERE 1=1 ");
        if (!string.IsNullOrEmpty(kind))
        {
            sb.Append("AND type = @kind ");
        }
        if (!string.IsNullOrEmpty(recipient))
        {
            sb.Append("AND recipient = @rec ");
        }
        if (!string.IsNullOrEmpty(q))
        {
            sb.Append("AND (ru_contains(product_name, @q) OR ru_contains(note, @q)) ");
        }
        sb.Append("ORDER BY id DESC ");
        if (limit > 0)
        {
            sb.Append($"LIMIT {limit} ");
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sb.ToString();
        if (!string.IsNullOrEmpty(kind)) cmd.Parameters.AddWithValue("@kind", kind);
        if (!string.IsNullOrEmpty(recipient)) cmd.Parameters.AddWithValue("@rec", recipient);
        if (!string.IsNullOrEmpty(q)) cmd.Parameters.AddWithValue("@q", q);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Operation
            {
                Id = reader.GetInt32(0),
                ProductId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                ProductName = reader.GetString(2),
                Unit = reader.GetString(3),
                Type = reader.GetString(4),
                Qty = reader.GetInt32(5),
                Recipient = reader.GetString(6),
                Note = reader.GetString(7),
                Date = reader.GetString(8)
            });
        }

        return list;
    }

    public List<string> GetRecipients()
    {
        var list = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT recipient FROM ops WHERE recipient <> '' ORDER BY recipient;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    #endregion

    #region Экспорт в CSV

    public void ExportToCsv(string filePath, IEnumerable<Operation> rows)
    {
        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        writer.WriteLine("ID;Дата;Товар;Операция;Количество;Ед.;Получатель;Примечание");
        foreach (var r in rows)
        {
            writer.WriteLine($"{r.Id};{r.FormattedDate};{EscapeCsv(r.ProductName)};{r.TypeName};{r.Qty};{r.Unit};{EscapeCsv(r.Recipient)};{EscapeCsv(r.Note)}");
        }
    }

    public void ExportOrderCsv(string filePath, IEnumerable<Product> rows)
    {
        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        writer.WriteLine("ID;Наименование;Категория;Ед.;Текущий остаток;Мин. норма;Нехватка;Рекомендуемый заказ");
        foreach (var p in rows)
        {
            writer.WriteLine($"{p.Id};{EscapeCsv(p.Name)};{EscapeCsv(p.Category)};{p.Unit};{p.Qty};{p.MinQty};{p.DeficitQty};{p.RecommendedOrderQty}");
        }
    }

    private static string EscapeCsv(string val)
    {
        if (string.IsNullOrEmpty(val)) return "";
        if (val.Contains(';') || val.Contains('"') || val.Contains('\n') || val.Contains('\r'))
        {
            return $"\"{val.Replace("\"", "\"\"")}\"";
        }
        return val;
    }

    #endregion

    #region Демо-данные

    public void SeedDemo(bool force = false)
    {
        if (!force)
        {
            using var checkCmd = _connection.CreateCommand();
            checkCmd.CommandText = "SELECT 1 FROM products LIMIT 1;";
            var exists = checkCmd.ExecuteScalar();
            if (exists != null)
            {
                return;
            }
        }

        var demoProducts = new[]
        {
            ("Бумага SvetoCopy A4 (80г/м², 500 л.)", "Бумага", "пачка", 18, 5),
            ("Бумага для заметок 76x76", "Бумага", "шт.", 4, 10),
            ("Ручка шариковая синяя (0.7 мм)", "Письменные", "шт.", 45, 15),
            ("Карандаш чернографитный HB", "Письменные", "шт.", 30, 10),
            ("Маркер перманентный черный", "Письменные", "шт.", 12, 5),
            ("Степлер металлический №24/6", "Офис", "шт.", 5, 2),
            ("Скобы для степлера №24/6", "Офис", "уп.", 20, 5),
            ("Клей-карандаш 21г", "Офис", "шт.", 15, 5),
            ("Папка-регистратор 75мм", "Папки", "шт.", 18, 5),
            ("Файлы-вкладыши А4 (100 шт.)", "Папки", "пачка", 10, 5)
        };

        var existingNames = new HashSet<string>(GetProducts().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var (name, cat, unit, qty, minQty) in demoProducts)
        {
            if (!existingNames.Contains(name))
            {
                SaveProduct(null, name, cat, unit, minQty, qty);
            }
        }

        var demoEmployees = new[]
        {
            ("Иванова Анна Петровна", "Бухгалтерия"),
            ("Смирнов Дмитрий Олегович", "Отдел продаж"),
            ("Кузнецов Алексей Сергеевич", "ИТ-отдел"),
            ("Морозова Елена Викторовна", "Отдел кадров"),
            ("Соколов Максим Андреевич", "Маркетинг")
        };

        var existingEmps = new HashSet<string>(GetEmployees().Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, dept) in demoEmployees)
        {
            if (!existingEmps.Contains(name))
            {
                SaveEmployee(null, name, dept);
            }
        }

        // Добавляем реалистичные операции выдачи и списания
        var allProds = GetProducts();
        var allEmps = GetEmployees();

        if (allProds.Count > 0 && allEmps.Count > 0)
        {
            var pPaper = allProds.FirstOrDefault(p => p.Name.Contains("SvetoCopy"));
            var pPen = allProds.FirstOrDefault(p => p.Name.Contains("синяя"));
            var pNote = allProds.FirstOrDefault(p => p.Name.Contains("заметок"));
            var pStapler = allProds.FirstOrDefault(p => p.Name.Contains("Степлер"));
            var pFolder = allProds.FirstOrDefault(p => p.Name.Contains("Папка-регистратор"));

            var empBuh = allEmps.FirstOrDefault(e => e.Dept.Contains("Бухгалтер")) ?? allEmps[0];
            var empSales = allEmps.FirstOrDefault(e => e.Dept.Contains("продаж")) ?? allEmps[0];
            var empIt = allEmps.FirstOrDefault(e => e.Dept.Contains("ИТ")) ?? allEmps[0];

            try
            {
                if (pPaper != null && pPaper.Qty > 2) Operate("out", pPaper.Id, 2, empBuh.Id, "Печать квартальной отчетности");
                if (pPen != null && pPen.Qty > 5) Operate("out", pPen.Id, 5, empSales.Id, "Выездная встреча с клиентами");
                if (pNote != null && pNote.Qty > 1) Operate("out", pNote.Id, 1, empIt.Id, "Для заметок и задач");
                if (pFolder != null && pFolder.Qty > 2) Operate("out", pFolder.Id, 2, empBuh.Id, "Архивирование первичных документов");
                if (pStapler != null && pStapler.Qty > 1) Operate("writeoff", pStapler.Id, 1, null, "Механический износ прижимной планки");
            }
            catch { }
        }
    }

    #endregion

    public void ResetDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ops; DELETE FROM products; DELETE FROM employees; VACUUM;";
        cmd.ExecuteNonQuery();
        _isDirty = true;
    }

    public void Dispose()
    {
        if (_connection != null)
        {
            try
            {
                AutoBackup(onlyIfDirty: true);
            }
            catch { }

            _connection.Dispose();
        }
    }
}
