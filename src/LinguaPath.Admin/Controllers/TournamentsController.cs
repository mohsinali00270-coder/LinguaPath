using LinguaPath.Admin.Infrastructure;
using LinguaPath.Admin.Models;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Application.Features.Competitions;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace LinguaPath.Admin.Controllers;

/// <summary>FR-Q11: create and schedule tournaments, choose the question pool, set prizes, view results, disqualify cheaters.</summary>
[Authorize(Policy = Policies.ContentManagers)]
public sealed class TournamentsController(ITournamentAdminService tournaments, ILanguageAdminService languages) : AdminControllerBase
{
    public async Task<IActionResult> Index(TournamentStatus? status, CancellationToken ct)
    {
        ViewBag.Status = status;
        return View(await tournaments.ListAsync(status, ct));
    }

    public async Task<IActionResult> Create(CancellationToken ct)
    {
        await LanguagesAsync(null, ct);
        return View("Edit", new TournamentInput());
    }

    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var d = await tournaments.DetailsAsync(id, ct);
        if (d is null) return NotFound();
        var t = d.Tournament;
        await LanguagesAsync(t.LanguageId, ct);
        return View(new TournamentInput
        {
            Id = t.Id, Title = t.Title, Description = t.Description, LanguageId = t.LanguageId, Level = t.Level, ModuleId = t.ModuleId,
            EntryOpensAtUtc = t.EntryOpensAt.UtcDateTime, StartsAtUtc = t.StartsAt.UtcDateTime, Rounds = t.Rounds, QuestionCount = t.QuestionCount,
            SecondsPerQuestion = t.SecondsPerQuestion, MaxEntrants = t.MaxEntrants, PrizeFirst = t.PrizeFirst, PrizeSecond = t.PrizeSecond,
            PrizeThird = t.PrizeThird
        });
    }

    [HttpPost]
    public async Task<IActionResult> Save(TournamentInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            await LanguagesAsync(input.LanguageId, ct);
            return View("Edit", input);
        }
        var form = new TournamentForm(input.Title, input.Description, input.LanguageId, input.Level, input.ModuleId,
            new DateTimeOffset(DateTime.SpecifyKind(input.EntryOpensAtUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(input.StartsAtUtc, DateTimeKind.Utc)),
            input.Rounds, input.QuestionCount, input.SecondsPerQuestion, input.MaxEntrants, input.PrizeFirst, input.PrizeSecond, input.PrizeThird);
        var result = await tournaments.SaveAsync(input.Id == 0 ? null : input.Id, form, ct);
        if (result.IsFailure)
        {
            AddErrors(result.Error!);
            await LanguagesAsync(input.LanguageId, ct);
            return View("Edit", input);
        }
        Success("Tournament saved. Publish it when it's ready for learners to join.");
        return RedirectToAction(nameof(Details), new { id = result.Value });
    }

    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var d = await tournaments.DetailsAsync(id, ct);
        return d is null ? NotFound() : View(d);
    }

    [HttpPost]
    public async Task<IActionResult> Publish(int id, CancellationToken ct) =>
        Done(await tournaments.PublishAsync(id, ct), id, "Published: learners can join from the entry time.");

    [HttpPost]
    public async Task<IActionResult> StartNow(int id, CancellationToken ct) =>
        Done(await tournaments.StartNowAsync(id, ct), id, "Round 1 has started.");

    [HttpPost]
    public async Task<IActionResult> Cancel(int id, string? reason, CancellationToken ct) =>
        Done(await tournaments.CancelAsync(id, reason ?? "", ct), id, "Tournament cancelled.");

    [HttpPost]
    public async Task<IActionResult> Disqualify(int id, Guid userId, string? reason, CancellationToken ct) =>
        Done(await tournaments.DisqualifyAsync(id, userId, reason ?? "", ct), id, "Disqualified; their tournament points were revoked and prizes moved up.");

    private IActionResult Done(LinguaPath.Application.Common.Result result, int id, string message)
    {
        if (result.IsFailure) Failure(result.Error!.Message);
        else Success(message);
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task LanguagesAsync(int? selected, CancellationToken ct) =>
        ViewBag.Languages = new SelectList((await languages.OptionsAsync(ct)).Select(l => new { l.Id, l.Name }), "Id", "Name", selected);
}
