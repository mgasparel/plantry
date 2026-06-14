using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Plantry.Web.Pages.Recipes;

[Authorize]
public sealed class IndexModel : PageModel
{
}
