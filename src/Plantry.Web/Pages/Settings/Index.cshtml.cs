using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Plantry.Web.Pages.Settings;

[Authorize]
public sealed class IndexModel : PageModel
{
    public void OnGet() { }
}
