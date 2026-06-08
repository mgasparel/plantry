using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Plantry.Web.Pages.Catalog;

[Authorize]
public sealed class IndexModel : PageModel
{
}
