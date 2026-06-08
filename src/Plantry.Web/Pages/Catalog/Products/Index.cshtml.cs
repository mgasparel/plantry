using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Catalog.Application;

namespace Plantry.Web.Pages.Catalog.Products;

[Authorize]
public sealed class IndexModel(ProductQueryService products) : PageModel
{
    public IReadOnlyList<ProductListItem> Products { get; private set; } = [];

    public async Task OnGetAsync() => Products = await products.ListActiveAsync();
}
