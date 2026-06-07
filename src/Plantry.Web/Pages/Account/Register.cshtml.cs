using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Tenancy;

namespace Plantry.Web.Pages.Account;

public sealed class RegisterModel(
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    IHouseholdRepository householdRepository,
    IEnumerable<IReferenceDataSeeder> seeders,
    TenantContext tenant,
    IClock clock) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [Required, MaxLength(200)]
        [Display(Name = "Household name")]
        public string HouseholdName { get; set; } = string.Empty;

        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        [Display(Name = "Your name")]
        public string DisplayName { get; set; } = string.Empty;

        [Required, MinLength(8)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Pantry/Index");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        // 1. Create household + seed reference data
        var cmd = new RegisterHouseholdCommand(Input.HouseholdName, clock, householdRepository, seeders, tenant);
        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return Page();
        }

        // 2. Create Identity user and stamp household claim
        var user = new AppUser
        {
            UserName = Input.Email,
            Email = Input.Email,
            DisplayName = Input.DisplayName,
            HouseholdId = result.Value.Value
        };

        var identityResult = await userManager.CreateAsync(user, Input.Password);
        if (!identityResult.Succeeded)
        {
            foreach (var error in identityResult.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        await userManager.AddClaimAsync(user, new System.Security.Claims.Claim(
            HouseholdIdClaims.ClaimType, result.Value.Value.ToString()));

        await signInManager.SignInAsync(user, isPersistent: false);
        return RedirectToPage("/Pantry/Index");
    }
}
