using System;
using System.Collections.Generic;

namespace RepoCleanup.Infrastructure.Clients.Altinn2
{
    public class Altinn2Service
    {
        public string ServiceOwnerCode { get; set; }

        public string OrganizationNumber { get; set; }

        public string ServiceOwnerName { get; set; }

        public string ServiceName { get; set; }

        public string ServiceCode { get; set; }

        public int ServiceEditionCode { get; set; }

        public DateTime ValidFrom { get; set; }

        public DateTime ValidTo { get; set; }

        public string ServiceType { get; set; }

        public bool EnterpriseUserEnabled { get; set; }

        public List<Altinn2Form> Forms { get; set; }
    }
}
