using System;
using System.Linq;
using System.Windows;
using Kancelyariya.Models;
using Kancelyariya.Services;

namespace Kancelyariya.Views;

public partial class EmployeeDialog : Window
{
    private readonly DatabaseStore _store;
    private readonly int? _employeeId;

    public EmployeeDialog(DatabaseStore store, int? employeeId = null)
    {
        InitializeComponent();
        _store = store;
        _employeeId = employeeId;

        if (_employeeId.HasValue)
        {
            Title = "Изменить сотрудника";
            var emp = _store.GetEmployees().FirstOrDefault(e => e.Id == _employeeId.Value);
            if (emp != null)
            {
                TxtName.Text = emp.Name;
                TxtDept.Text = emp.Dept;
            }
        }
        else
        {
            Title = "Новый сотрудник";
        }

        Loaded += (_, _) => TxtName.Focus();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;

        string name = TxtName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Укажите ФИО сотрудника.");
            return;
        }

        string dept = TxtDept.Text?.Trim() ?? "";

        try
        {
            _store.SaveEmployee(_employeeId, name, dept);
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
