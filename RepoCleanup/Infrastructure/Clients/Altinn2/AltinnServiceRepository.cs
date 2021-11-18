using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace RepoCleanup.Infrastructure.Clients.Altinn2
{
    /// <summary>
    /// A simple tool for downloading metadata informasion about services in Altinn 2.
    /// </summary>
    public class AltinnServiceRepository
    {
        private const string metadataApi = "https://www.altinn.no/api/metadata";
        private static HttpClient client = new HttpClient();

        /// <summary>
        /// Gets all active reporting services from Altinn 2
        /// </summary>
        /// <returns>A list with all active reporting services from Altinn 2</returns>
        public static async Task<List<Altinn2Service>> GetReportingServices()
        {
            List<Altinn2Service> reportingServices = null;

            string path = $"{metadataApi}?$filter=ServiceType%20eq%20%27FormTask%27";

            HttpResponseMessage response = await client.GetAsync(path);

            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync();
                reportingServices = JsonSerializer.Deserialize<List<Altinn2Service>>(content);
            }

            return reportingServices ?? new List<Altinn2Service>();
        }

        /// <summary>
        /// Gets the metadata for a specific reporting service from Altinn 2
        /// </summary>
        /// <param name="altinn2Service">The Altinn 2 service description.</param>
        /// <returns>The reporting service metadata description.</returns>
        public static async Task<Altinn2ReportingService> GetReportingService(Altinn2Service altinn2Service)
        {
            string path = ReportingServiceMetadataUrl(altinn2Service);
            Altinn2ReportingService reportingService = null;

            HttpResponseMessage response = await client.GetAsync(path);
            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync();
                reportingService = JsonSerializer.Deserialize<Altinn2ReportingService>(content);
            }

            return reportingService;
        }

        /// <summary>
        /// Get the XSD for a specific form.
        /// </summary>
        /// <param name="altinn2Service">The service metadata description.</param>
        /// <param name="formMetaData">The form metadata description.</param>
        /// <returns>The XSD loaded into an <see cref="XDocument"/></returns>
        public static async Task<XDocument> GetFormXsd(Altinn2Service altinn2Service, Altinn2Form formMetaData)
        {
            string path = FormXsdUrl(altinn2Service, formMetaData);

            using HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, path);
            requestMessage.Headers.Add("Accept", "application/xml");

            HttpResponseMessage response = await client.SendAsync(requestMessage);

            if (response.IsSuccessStatusCode)
            {
                XDocument doc = await XDocument.LoadAsync(
                    await response.Content.ReadAsStreamAsync(),
                    LoadOptions.None,
                    CancellationToken.None);

                return doc;
            }

            return null;
        }

        private static string ReportingServiceMetadataUrl(Altinn2Service altinnResource)
        {
            return $"{metadataApi}/formtask/{altinnResource.ServiceCode}/{altinnResource.ServiceEditionCode}";
        }

        private static string FormXsdUrl(Altinn2Service altinnResource, Altinn2Form formMetaData)
        {
            return ReportingServiceMetadataUrl(altinnResource)
                + $"/forms/{formMetaData.DataFormatID}/{formMetaData.DataFormatVersion}/xsd";
        }
    }
}
