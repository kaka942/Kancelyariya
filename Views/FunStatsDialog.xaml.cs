using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kancelyariya.Services;

namespace Kancelyariya.Views;

public partial class FunStatsDialog : Window
{
    private static readonly Random _rnd = new();

    private static readonly string[] _memos =
    [
        "Генеральному директору.\nОбъяснительная: Степлер №24/6 самовольно покинул рабочее место в пятницу в 17:59 в поисках скоб повышенной прочности. По оперативным данным очевидцев, он был замечен в отделе маркетинга, где выдавал себя за скотч. Обязуюсь принести из дома синюю ручку.",
        "Руководству департамента.\nОбъяснительная: Черный маркер исчез не по моей вине. Он был завербован бухгалтерией для закрашивания нестыковок в балансе за прошлый квартал. Свою причастность отрицаю, готов пройти проверку на полиграфе.",
        "Заведующему складом.\nОбъяснительная: Пачка бумаги А4 растворилась в пространственно-временном континууме офисного принтера после нажатия кнопки «Печать 2-х экземпляров договора». Принтер издал сытый звук и замигал желтым. Виноват принтер.",
        "Начальнику отдела логистики.\nОбъяснительная: Пропавший клей-карандаш был применен для склеивания разбитых надежд на сдачу отчета до дедлайна. Результат признан частично удовлетворительным: отчет склеен, совесть чиста.",
        "Службе безопасности офиса.\nОбъяснительная: Канцелярские скрепки не пропали, они объединились в единую замкнутую цепь и ушли медитировать на кулер. При попытке вернуть их на место выразили молчаливый протест."
    ];

    public FunStatsDialog(DatabaseStore store)
    {
        InitializeComponent();

        TxtUserRank.Text = "Специалист по учету";

        ComputeStats(store);
        GenerateMemo();
    }

    private void ComputeStats(DatabaseStore store)
    {
        var ops = store.GetJournal();
        var outOps = ops.Where(o => o.Type == "out").ToList();

        if (outOps.Count > 0)
        {
            var topRecipient = outOps
                .GroupBy(o => o.Recipient)
                .OrderByDescending(g => g.Sum(x => x.Qty))
                .FirstOrDefault();

            TxtTopConsumer.Text = topRecipient != null ? $"{topRecipient.Key} ({topRecipient.Sum(x => x.Qty)} шт.)" : "Бухгалтерия";

            var topProduct = outOps
                .GroupBy(o => o.ProductName)
                .OrderByDescending(g => g.Sum(x => x.Qty))
                .FirstOrDefault();

            TxtPopularProduct.Text = topProduct != null ? $"{topProduct.Key} ({topProduct.Sum(x => x.Qty)} шт.)" : "Бумага А4";
        }
        else
        {
            TxtTopConsumer.Text = "Пока никто (склад неприкосновенен)";
            TxtPopularProduct.Text = "Бумага SvetoCopy A4";
        }

        var emps = store.GetEmployees();
        if (emps.Count > 0)
        {
            var depts = emps.Select(e => e.Dept).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().ToList();
            var deptCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in depts) deptCounts[d] = 0;

            foreach (var op in outOps)
            {
                foreach (var d in depts)
                {
                    if (op.Recipient.Contains(d, StringComparison.OrdinalIgnoreCase))
                    {
                        deptCounts[d]++;
                    }
                }
            }

            var quietest = depts.OrderBy(d => deptCounts[d]).FirstOrDefault();
            TxtThriftyDept.Text = quietest ?? "Юридический отдел";
        }
        else
        {
            TxtThriftyDept.Text = "ИТ-отдел";
        }
    }

    private void GenerateMemo()
    {
        int index = _rnd.Next(_memos.Length);
        TxtMemo.Text = _memos[index];
    }

    private void OnGenerateMemoClick(object sender, RoutedEventArgs e)
    {
        GenerateMemo();
        TxtCopiedStatus.Visibility = Visibility.Collapsed;
    }

    private void OnCopyMemoClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TxtMemo.Text);
            TxtCopiedStatus.Visibility = Visibility.Visible;
        }
        catch { }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
