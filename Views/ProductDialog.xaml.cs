using System;
using System.Collections.Generic;
using System.Windows;
using Kancelyariya.Models;
using Kancelyariya.Services;

namespace Kancelyariya.Views;

public partial class ProductDialog : Window
{
    private readonly DatabaseStore _store;
    private readonly int? _productId;

    public ProductDialog(DatabaseStore store, int? productId = null)
    {
        InitializeComponent();
        _store = store;
        _productId = productId;

        CmbCategory.ItemsSource = _store.GetCategories();
        CmbUnit.ItemsSource = DatabaseStore.Units;

        if (_productId.HasValue)
        {
            Title = "Изменить товар";
            LblInitialQty.Visibility = Visibility.Collapsed;
            TxtInitialQty.Visibility = Visibility.Collapsed;

            var p = _store.GetProduct(_productId.Value);
            if (p != null)
            {
                TxtName.Text = p.Name;
                CmbCategory.Text = p.Category;
                CmbUnit.Text = p.Unit;
                TxtMinQty.Text = p.MinQty.ToString();
            }
        }
        else
        {
            Title = "Новый товар";
            CmbUnit.SelectedIndex = 0;
        }

        Loaded += (_, _) => TxtName.Focus();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;

        string name = TxtName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Укажите название товара.");
            return;
        }

        if (!int.TryParse(TxtMinQty.Text.Trim(), out int minQty) || minQty < 0)
        {
            ShowError("Минимальный остаток — целое неотрицательное число.");
            return;
        }

        int initialQty = 0;
        if (!_productId.HasValue)
        {
            if (!int.TryParse(TxtInitialQty.Text.Trim(), out initialQty) || initialQty < 0)
            {
                ShowError("Начальное количество — целое неотрицательное число.");
                return;
            }
        }

        string category = CmbCategory.Text?.Trim() ?? "";
        string unit = string.IsNullOrWhiteSpace(CmbUnit.Text) ? "шт." : CmbUnit.Text.Trim();

        try
        {
            _store.SaveProduct(_productId, name, category, unit, minQty, initialQty);
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
