using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Plantry.Web.Pages.Catalog.Products;

/// <summary>
/// Catalog's standalone Products grid was absorbed into the unified Pantry list (plantry-sjfn) — its
/// columns were a strict subset of Pantry's, and having two entry points for "products" was a
/// structural stumbling block. This route now just redirects to <c>/Pantry</c> (Everything scope
/// shows every active product, stocked or not) so old links/bookmarks/nav muscle-memory keep
/// working instead of 404ing. <c>/Catalog/Products/{id}</c> (the definition-form detail page) and
/// <c>/Catalog/Products/Create</c> are unaffected — only this list page is retired.
/// </summary>
[Authorize]
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Pantry/Index");
}
