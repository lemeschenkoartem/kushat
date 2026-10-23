namespace Kushat.Core;

public enum LabelStyle { Classic, LargeHundredGrams, Accent, Chalkboard, Cafe }

public sealed record LabelTemplate(string Id, string Name, string Description, LabelStyle Style)
{
    public override string ToString() => Name;
}

public static class LabelTemplates
{
    public static IReadOnlyList<LabelTemplate> All { get; } = Array.AsReadOnly(new[]
    {
        new LabelTemplate("classic", "Классический", "Название сверху, цена снизу: за 100 г для весовых, за штуку для штучных.", LabelStyle.Classic),
        new LabelTemplate("large-100g", "Крупная цена", "Крупная цена за 100 г для весовых или за штуку для штучных товаров.", LabelStyle.LargeHundredGrams),
        new LabelTemplate("accent", "С акцентной полосой", "Зелёная полоса, название сверху и цена за 100 г или штуку снизу.", LabelStyle.Accent),
        new LabelTemplate("chalkboard", "Меловой · чёрный фон", "Белые надписи на чёрном фоне, название курсивом.", LabelStyle.Chalkboard),
        new LabelTemplate("cafe", "Кафе · полосатая рамка", "Чёрно-белое оформление с полосатым навесом и рамкой.", LabelStyle.Cafe)
    });

    public static LabelTemplate Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}
