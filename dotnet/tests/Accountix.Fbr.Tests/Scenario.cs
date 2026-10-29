using Accountix.TestSupport;

namespace Accountix.Fbr.Tests;

/// <summary>
/// One sequence of FBR requests (Send, Retry, Validate, ...) played through
/// the app with fixed FBR answers: the answers to the browser, what was sent
/// to FBR and the rows kept. The Python app played the same sequences on
/// 29 Oct 2026 (Golden/capture/SubmissionScenario.py); its results are the
/// fixtures the tests compare with. In URLs, {rec} is the latest FBR record
/// of <see cref="Step.Invoice"/>.
/// </summary>
public static class Scenario
{
    public sealed record Answer(int Status, string Body, string ContentType = "application/json");

    public sealed record Step(string Method, string Url, int Invoice, Answer[]? Answers = null, Dictionary<string, string>? Form = null);

    public sealed record Response(int Status, string Body, string? Location);

    public sealed record Sent(string Method, string Url, string? Body, string? Auth);

    public sealed record Result(List<Response> Responses, List<Sent> Sent);

    public static async Task<Result> DotnetAsync(IReadOnlyList<Step> steps, PageClient client)
    {
        string token = await client.TokenFromAsync("/fbr/");
        var responses = new List<Response>();
        foreach (var step in steps)
        {
            FakeFbr.Reset();
            foreach (var a in step.Answers ?? [])
                FakeFbr.Then(new FakeFbr.Reply(a.Status, a.Body, a.ContentType));
            string url = step.Url;
            if (url.Contains("{rec}"))
                url = url.Replace("{rec}", (await FbrData.RecordsAsync(step.Invoice)).Last().Id.ToString());
            HttpResponseMessage r;
            if (step.Method == "POST")
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new FormUrlEncodedContent((step.Form ?? []).Select(kv => kv)),
                };
                req.Headers.Add("X-CSRF-Token", token);
                r = await client.SendAsync(req);
            }
            else
                r = await client.GetAsync(url);
            responses.Add(new Response((int)r.StatusCode, await r.Content.ReadAsStringAsync(), r.Headers.Location?.OriginalString));
            _sent.AddRange(FakeFbr.Calls.Select(c => new Sent(c.Method, c.Url, c.Body, c.Authorization)));
        }
        var sent = _sent.ToList();
        _sent.Clear();
        return new Result(responses, sent);
    }

    private static readonly List<Sent> _sent = [];
}
