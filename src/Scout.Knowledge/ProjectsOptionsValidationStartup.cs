using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Scout.Knowledge;

/// <summary>
/// Runs <see cref="ProjectsOptionsValidator"/> once at startup so malformed
/// configured projects surface as warnings before any indexing or chat begins.
/// Does no I/O beyond the validator's existence checks and never throws.
/// </summary>
internal sealed class ProjectsOptionsValidationStartup : IHostedService
{
    private readonly ProjectsOptionsValidator validator;
    private readonly IOptions<ProjectsOptions> options;

    public ProjectsOptionsValidationStartup(
        ProjectsOptionsValidator validator,
        IOptions<ProjectsOptions> options)
    {
        this.validator = validator;
        this.options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.validator.Validate(this.options.Value);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
