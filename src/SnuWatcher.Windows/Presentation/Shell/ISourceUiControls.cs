using SnuWatcher.Application.Monitoring.SourcePolicy;

namespace SnuWatcher.Windows.Presentation.Shell;

/// <summary>Explicit host-owned source controls; neither operation is a course lookup or a policy bypass.</summary>
public interface ISourceUiControls
{
    /// <summary>Resume against the displayed durable policy revision; an existing cooldown remains authoritative.</summary>
    Task<SourceAccessResumeResult> ResumeSourceAccessAsync(long expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>Retry local source-policy finalization through its owning host.</summary>
    Task RecoverSourcePolicyAsync(CancellationToken cancellationToken = default);
}
