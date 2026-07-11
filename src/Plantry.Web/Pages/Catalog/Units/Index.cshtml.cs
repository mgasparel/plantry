using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Catalog.Units;

[Authorize]
public sealed class IndexModel(
    IUnitRepository units,
    ITenantContext tenant,
    ILogger<CreateUnitCommand> createUnitLogger,
    ILogger<SetDisplayStyleCommand> setDisplayStyleLogger,
    ILogger<SetUnitSystemCommand> setUnitSystemLogger) : PageModel
{
    public IReadOnlyList<Unit> Units { get; private set; } = [];

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [Required, MaxLength(20)]
        [Display(Name = "Code")]
        public string Code { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        [Display(Name = "Full name")]
        public string Name { get; set; } = string.Empty;

        [Required]
        public Dimension Dimension { get; set; }

        [Required, Range(0.000001, double.MaxValue, ErrorMessage = "Factor must be positive.")]
        [Display(Name = "Factor to base unit")]
        public decimal FactorToBase { get; set; } = 1m;
    }

    public async Task OnGetAsync() => Units = await units.ListAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            Units = await units.ListAsync();
            return Page();
        }

        var cmd = new CreateUnitCommand(Input.Code, Input.Name, Input.Dimension, Input.FactorToBase, isBase: false, units, tenant, createUnitLogger);
        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            Units = await units.ListAsync();
            return Page();
        }

        return RedirectToPage();
    }

    /// <summary>
    /// htmx handler for the per-unit display-style toggle (quantity-display.md Q2). Persists the
    /// choice and returns the re-rendered control fragment for an outerHTML swap.
    /// </summary>
    public async Task<IActionResult> OnPostDisplayStyleAsync(Guid unitId, DisplayStyle style)
    {
        var id = UnitId.From(unitId);
        var result = await new SetDisplayStyleCommand(id, style, units, tenant, setDisplayStyleLogger).ExecuteAsync();
        if (result.IsFailure)
            return result.Error.Code == Error.NotFound.Code ? NotFound() : Forbid();

        var unit = await units.FindAsync(id);
        if (unit is null)
            return NotFound();

        return Partial("_UnitDisplayStyleControl", unit);
    }

    /// <summary>
    /// htmx handler for the per-unit measurement-system selector (quantity-display.md Q5) — the
    /// metric/imperial simplification firewall. Persists the choice and returns the re-rendered control
    /// fragment for an outerHTML swap.
    /// </summary>
    public async Task<IActionResult> OnPostUnitSystemAsync(Guid unitId, UnitSystem system)
    {
        var id = UnitId.From(unitId);
        var result = await new SetUnitSystemCommand(id, system, units, tenant, setUnitSystemLogger).ExecuteAsync();
        if (result.IsFailure)
            return result.Error.Code == Error.NotFound.Code ? NotFound() : Forbid();

        var unit = await units.FindAsync(id);
        if (unit is null)
            return NotFound();

        return Partial("_UnitSystemControl", unit);
    }
}
