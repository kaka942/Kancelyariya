using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Kancelyariya.Models;
using Kancelyariya.Services;

namespace Kancelyariya.Views;

public partial class OperationDialog : Window
{
    private readonly DatabaseStore _store;
    private readonly List<Product> _products;
    private readonly List<Employee> _employees;

    private class TypeItem
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public override string ToString() => Title;
    }

    public OperationDialog(DatabaseStore store, string initialKind = "in", int? initialProductId = null)
    {
        InitializeComponent();
        _store = store;

        _products = _store.GetProducts();
        _employees = _store.GetEmployees();

        CmbProduct.ItemsSource = _products;
        CmbEmployee.ItemsSource = _employees;

        var typeItems = new List<TypeItem>
        {
            new() { Key = "in", Title = "Приход" },
            new() { Key = "out", Title = "Выдача" },
            new() { Key = "writeoff", Title = "Списание" }
        };
        CmbType.ItemsSource = typeItems;

        var selType = typeItems.FirstOrDefault(t => t.Key == initialKind) ?? typeItems[0];
        CmbType.SelectedItem = selType;

        if (initialProductId.HasValue)
        {
            var p = _products.FirstOrDefault(x => x.Id == initialProductId.Value);
            if (p != null) CmbProduct.SelectedItem = p;
        }

        if (CmbProduct.SelectedItem == null && _products.Count > 0)
        {
            CmbProduct.SelectedIndex = 0;
        }

        if (_employees.Count > 0)
        {
            CmbEmployee.SelectedIndex = 0;
        }

        UpdateFormState();

        Loaded += (_, _) =>
        {
            TxtQty.Focus();
            TxtQty.SelectAll();
        };
    }

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFormState();
    }

    private void OnProductChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStockInfo();
    }

    private void UpdateFormState()
    {
        if (CmbType.SelectedItem is not TypeItem curType) return;

        Title = curType.Title;

        bool isOut = curType.Key == "out";
        LblEmployee.Visibility = isOut ? Visibility.Visible : Visibility.Collapsed;
        CmbEmployee.Visibility = isOut ? Visibility.Visible : Visibility.Collapsed;

        LblNote.Text = curType.Key switch
        {
            "in" => "Поставщик / накладная:",
            "out" => "Примечание:",
            "writeoff" => "Причина (обязательно):",
            _ => "Примечание:"
        };

        UpdateStockInfo();
    }

    private void UpdateStockInfo()
    {
        if (CmbProduct.SelectedItem is Product p)
        {
            // Берем актуальный остаток из базы
            var latest = _store.GetProduct(p.Id);
            if (latest != null)
            {
                TxtStock.Text = $"На складе: {latest.Qty} {latest.Unit}";
                return;
            }
        }
        TxtStock.Text = "";
    }

    private void OnExecuteClick(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;

        if (CmbType.SelectedItem is not TypeItem curType)
        {
            ShowError("Выберите операцию.");
            return;
        }

        if (CmbProduct.SelectedItem is not Product selectedProd)
        {
            ShowError("Выберите товар.");
            return;
        }

        if (!int.TryParse(TxtQty.Text.Trim(), out int qty) || qty < 1)
        {
            ShowError("Количество должно быть целым числом больше нуля.");
            return;
        }

        int? empId = null;
        if (curType.Key == "out")
        {
            if (CmbEmployee.SelectedItem is Employee emp)
            {
                empId = emp.Id;
            }
            else
            {
                ShowError("Выберите сотрудника (при необходимости добавьте его на вкладке «Сотрудники»).");
                return;
            }
        }

        string note = TxtNote.Text?.Trim() ?? "";

        try
        {
            _store.Operate(curType.Key, selectedProd.Id, qty, empId, note);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string msg)
    {
        TxtError.Text = msg;
        TxtError.Visibility = Visibility.Visible;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
