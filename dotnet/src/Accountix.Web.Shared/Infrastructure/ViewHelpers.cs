using Accountix.Core.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Accountix.Web.Infrastructure;

public static class ViewHelpers
{
    /// <summary>The amount_format filter: money in the active company's number format, negatives in brackets.</summary>
    public static string Amount(this IHtmlHelper html, decimal? value, int decimals = 2) =>
        Money.Format(value, decimals, html.ViewContext.HttpContext.Access()?.NumberFormat);

    public static string Amount(this IHtmlHelper html, double? value, int decimals = 2) =>
        Money.Format(value, decimals, html.ViewContext.HttpContext.Access()?.NumberFormat);

    /// <summary>Money for JSON answers, in the active company's format.</summary>
    public static string Amount(this ControllerBase c, decimal? value, int decimals = 2) =>
        Money.Format(value, decimals, c.HttpContext.Access()?.NumberFormat);

    /// <summary>Render a (partial) view to a string — for JSON answers that carry HTML (render_template in Python).</summary>
    public static async Task<string> RenderViewAsync(this Controller c, string viewName, object? model, bool partial = true)
    {
        var engine = c.HttpContext.RequestServices.GetRequiredService<ICompositeViewEngine>();
        var result = engine.FindView(c.ControllerContext, viewName, isMainPage: !partial);
        if (!result.Success)
            throw new InvalidOperationException($"View '{viewName}' not found: {string.Join(", ", result.SearchedLocations)}");
        await using var writer = new StringWriter();
        var viewData = new ViewDataDictionary(c.ViewData) { Model = model };
        var ctx = new ViewContext(c.ControllerContext, result.View, viewData, c.TempData, writer, new HtmlHelperOptions());
        await result.View.RenderAsync(ctx);
        return writer.ToString();
    }

    /// <summary>Title case as Python's str.title() for single words (approved → Approved).</summary>
    public static string Title(string? s) =>
        string.IsNullOrEmpty(s) ? "" : string.Join(' ', s.Split(' ').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
}
