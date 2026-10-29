namespace Accountix.Core.Data;

/// <summary>Raised when company data is read or written with no company chosen.</summary>
public sealed class NoActiveCompanyException(string message) : InvalidOperationException(message);

/// <summary>Raised when a write would put a row into, or move it to, another company.</summary>
public sealed class CrossCompanyWriteException(string message) : InvalidOperationException(message);

/// <summary>
/// The company the current request works in (one instance per request).
/// Set once by the company-context middleware after it has checked that the
/// person is an active member of an active company; every company table is
/// filtered by it.
/// </summary>
public sealed class CompanyContext
{
    private int _bypass;

    public int? CompanyId { get; private set; }

    /// <summary>True inside <see cref="Unscoped"/>: company filters are off for deliberate platform-wide work.</summary>
    public bool Bypassed => _bypass > 0;

    public void Set(int? companyId) => CompanyId = companyId;

    /// <summary>
    /// Deliberate platform-wide access (super admin console, seeding, tools).
    /// Greppable on purpose: every use is a place to review.
    /// </summary>
    public IDisposable Unscoped()
    {
        _bypass++;
        return new Release(this);
    }

    private sealed class Release(CompanyContext owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner._bypass--;
        }
    }
}
