using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Plantry.Web.Pages.Today;

[Authorize]
public sealed class IndexModel : PageModel
{
    public void OnGet() { }
}
