using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CivilStream.Core;

namespace CivilStream.Core
{
    /// <summary>
    /// Service to query the public Open-Meteo API for elevation data.
    /// It batches up to 100 points per request (the API limit) and returns
    /// a list of CivilPoint objects with the retrieved Z values.
    /// </summary>
    public static class ElevationService
    {
        private const string ApiUrl = "https://api.open-meteo.com/v1/elevation";
        private static readonly HttpClient _http = new HttpClient();

        /// <summary>
        /// Retrieves elevations for the supplied geographic points.
        /// Input points must have Longitude as X and Latitude as Y (Z ignored).
        /// </summary>
        public static async Task<List<CivilPoint>> GetElevationsAsync(List<CivilPoint> geoPoints)
        {
            if (geoPoints == null) throw new ArgumentNullException(nameof(geoPoints));
            var result = new List<CivilPoint>();
            
            // Open-Meteo allows up to 100 locations per request – batch if needed.
            const int batchSize = 100;
            for (int i = 0; i < geoPoints.Count; i += batchSize)
            {
                var batch = geoPoints.GetRange(i, Math.Min(batchSize, geoPoints.Count - i));
                
                var lats = string.Join(",", batch.ConvertAll(p => p.Y.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)));
                var lons = string.Join(",", batch.ConvertAll(p => p.X.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)));
                
                string url = $"{ApiUrl}?latitude={lats}&longitude={lons}";
                
                var response = await _http.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync();
                
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                if (root.TryGetProperty("elevation", out var elevations))
                {
                    int index = 0;
                    foreach (var item in elevations.EnumerateArray())
                    {
                        double elev = item.GetDouble();
                        // Open-Meteo returns elevations in the exact order of the requested coordinates
                        var originalPoint = batch[index++];
                        result.Add(new CivilPoint(originalPoint.X, originalPoint.Y, elev));
                    }
                }
                else
                {
                    throw new Exception("Unexpected response format from Open-Meteo Elevation API.");
                }
            }
            return result;
        }
    }
}
