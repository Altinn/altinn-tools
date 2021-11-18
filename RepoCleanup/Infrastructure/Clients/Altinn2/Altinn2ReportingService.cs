using System.Collections.Generic;

namespace RepoCleanup.Infrastructure.Clients.Altinn2
{
    public class Altinn2ReportingService
    {
        public bool RestEnabled { get; set; }

        public List<Altinn2Form> FormsMetaData { get; set; }
    }
}
