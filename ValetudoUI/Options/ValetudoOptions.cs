using System.ComponentModel.DataAnnotations;

namespace ValetudoUI.Options;

public sealed class ValetudoOptions
{
    public const string Section = "Valetudo";

    /// <summary>Base URL of the Valetudo instance, e.g. http://192.168.1.50</summary>
    [Required]
    public string BaseUrl { get; set; } = "";

    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Development only: host a fake robot under /fake and point BaseUrl at it.</summary>
    public bool UseFakeRobot { get; set; }

    /// <summary>Development only: the fake robot starts a cleaning run of this many seconds right at startup.</summary>
    public int? FakeStartScenarioSeconds { get; set; }
}

public sealed class HistoryOptions
{
    public const string Section = "History";

    /// <summary>Runs shorter than this (and with almost no path) are discarded as noise.</summary>
    public int MinRunSeconds { get; set; } = 30;

    /// <summary>How often an in-progress run is checkpointed to the database.</summary>
    public int CheckpointSeconds { get; set; } = 60;

    /// <summary>How long a docked/idle robot with the "resumable" flag keeps the run open.</summary>
    public int ResumableGraceMinutes { get; set; } = 20;

    public string DbFileName { get; set; } = "valetudo-ui.db";
}
