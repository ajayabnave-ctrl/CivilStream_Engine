using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NetTopologySuite.Geometries;

namespace CivilStream.Core
{
    public static class SurveyParser
    {
        private static readonly char[] _separators = new[] { ',', '\t', ' ' };

        /// <summary>
        /// Parse a file based on its extension. Supports .csv/.txt (delimited), .xml (LandXML) and .kml (polygon).
        /// </summary>
        public static List<CivilPoint> ParseFile(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLower();
            switch (extension)
            {
                case ".xml":
                    return ParseLandXml(filePath);
                case ".kml":
                    var poly = ParseKmlPolygon(filePath);
                    return poly.Select(p => new CivilPoint(p.X, p.Y, p.Z)).ToList();
                default:
                    return ParseDelimitedText(filePath);
            }
        }

        private static List<CivilPoint> ParseDelimitedText(string filePath)
        {
            var points = new List<CivilPoint>();
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(_separators, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                // optional index column handling
                if (parts.Length >= 4 && double.TryParse(parts[1], out _) && double.TryParse(parts[2], out _) && double.TryParse(parts[3], out _))
                {
                    if (double.TryParse(parts[1], out double x) &&
                        double.TryParse(parts[2], out double y) &&
                        double.TryParse(parts[3], out double z))
                    {
                        points.Add(new CivilPoint(x, y, z));
                        continue;
                    }
                }
                if (double.TryParse(parts[0], out double x0) &&
                    double.TryParse(parts[1], out double y0) &&
                    double.TryParse(parts[2], out double z0))
                {
                    points.Add(new CivilPoint(x0, y0, z0));
                }
            }
            return points;
        }

        private static List<CivilPoint> ParseLandXml(string filePath)
        {
            var points = new List<CivilPoint>();
            var doc = XDocument.Load(filePath);
            var pointElements = doc.Descendants().Where(e => e.Name.LocalName == "P" || e.Name.LocalName == "CgPoint");
            foreach (var p in pointElements)
            {
                var parts = p.Value.Split(_separators, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 &&
                    double.TryParse(parts[0], out double x) &&
                    double.TryParse(parts[1], out double y) &&
                    double.TryParse(parts[2], out double z))
                {
                    points.Add(new CivilPoint(x, y, z));
                }
            }
            return points;
        }

        /// <summary>
        /// Parses a KML file containing a Polygon and returns its vertices as CivilPoint objects.
        /// X = longitude, Y = latitude, Z = altitude (or 0 if missing).
        /// </summary>
        public static List<CivilPoint> ParseKmlPolygon(string kmlPath)
        {
            if (!File.Exists(kmlPath))
                throw new FileNotFoundException($"KML file not found: {kmlPath}");

            var doc = XDocument.Load(kmlPath);
            var coordString = doc.Descendants()
                                 .FirstOrDefault(e => e.Name.LocalName == "coordinates")?.Value;
            if (string.IsNullOrWhiteSpace(coordString))
                throw new Exception("No <coordinates> element found in KML file.");

            var points = new List<CivilPoint>();
            var coordPairs = coordString.Trim().Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in coordPairs)
            {
                var parts = pair.Split(',');
                if (parts.Length < 2) continue;
                if (double.TryParse(parts[0], out double lon) && double.TryParse(parts[1], out double lat))
                {
                    double alt = 0;
                    if (parts.Length > 2) double.TryParse(parts[2], out alt);
                    points.Add(new CivilPoint(lon, lat, alt));
                }
            }
            return points;
        }
        public static List<CivilPoint> ParseAsciiGrid(string content, List<CivilPoint> boundaryPoints)
        {
            var points = new List<CivilPoint>();
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            int ncols = 0;
            int nrows = 0;
            double xllcorner = 0;
            double yllcorner = 0;
            double cellsize = 0;
            double nodata = -9999;
            
            int headerRows = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                
                string key = parts[0].ToLower();
                if (key == "ncols") { ncols = int.Parse(parts[1]); headerRows++; }
                else if (key == "nrows") { nrows = int.Parse(parts[1]); headerRows++; }
                else if (key == "xllcorner") { xllcorner = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture); headerRows++; }
                else if (key == "yllcorner") { yllcorner = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture); headerRows++; }
                else if (key == "cellsize") { cellsize = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture); headerRows++; }
                else if (key == "nodata_value") { nodata = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture); headerRows++; }
                else
                {
                    break;
                }
            }

            // Create NetTopologySuite polygon from projected boundary points for containment checks
            var geomFactory = new GeometryFactory();
            var shellCoords = boundaryPoints.Select(p => new Coordinate(p.X, p.Y)).ToArray();
            if (shellCoords.Length > 0 && !shellCoords[0].Equals2D(shellCoords[shellCoords.Length - 1]))
            {
                var list = shellCoords.ToList();
                list.Add(shellCoords[0]);
                shellCoords = list.ToArray();
            }
            var polyGeom = geomFactory.CreatePolygon(shellCoords);
            
            // Downsample if grid is too large to maintain fast UI rendering (<2500 active points)
            int targetSize = 50;
            int stepRow = (int)Math.Max(1, Math.Ceiling((double)nrows / targetSize));
            int stepCol = (int)Math.Max(1, Math.Ceiling((double)ncols / targetSize));
            
            for (int r = 0; r < nrows; r += stepRow)
            {
                int lineIndex = headerRows + r;
                if (lineIndex >= lines.Length) break;
                
                var rowVals = lines[lineIndex].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                for (int c = 0; c < ncols; c += stepCol)
                {
                    if (c >= rowVals.Length) break;
                    
                    if (double.TryParse(rowVals[c], System.Globalization.CultureInfo.InvariantCulture, out double elev))
                    {
                        if (Math.Abs(elev - nodata) < 0.1) continue;
                        
                        double lon = xllcorner + c * cellsize;
                        double lat = yllcorner + (nrows - 1 - r) * cellsize;
                        
                        var (localX, localY) = CoordinateProjection.ToLocal(lat, lon);
                        var testCoord = new Coordinate(localX, localY);
                        
                        if (polyGeom.Contains(geomFactory.CreatePoint(testCoord)))
                        {
                            points.Add(new CivilPoint(localX, localY, elev));
                        }
                    }
                }
            }
            return points;
        }
    }
}