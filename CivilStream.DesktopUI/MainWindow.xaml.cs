#pragma warning disable IDE0305 
#pragma warning disable IDE0090 
#pragma warning disable IDE0028 
#pragma warning disable IDE0300 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Controls;
using Microsoft.Win32;
using CivilStream.Core;
using HelixToolkit.Wpf;
using NetTopologySuite.Geometries;
using NetTopologySuite.Triangulate;
using System.IO;
using System.Globalization;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using System.Text.Json;

// Required for CAD Import & Export
using netDxf;

namespace CivilStream.DesktopUI
{
    public class ContourSegment
    {
        public Point3D Start { get; set; }
        public Point3D End { get; set; }
        public double Elevation { get; set; }
        public bool IsMajor { get; set; }
    }

    public partial class MainWindow : Window
    {
        private readonly List<System.Numerics.Vector3[]> _localTriangles = new();
        private readonly List<ContourSegment> _contourLines = new();

        private double _globalCenterX = 0;
        private double _globalCenterY = 0;
        private double _globalBaseZ = 0;

        private Material? _solidMaterial;
        private Material? _heatMaterial;
        private Material? _invisibleHitTestMaterial;
        private GeometryModel3D? _terrainModel;

        private bool _isHeatmapActive = true;
        private bool _isWireframeActive = false;

        private bool _isSatelliteAvailable = false;
        private bool _isSatelliteActive = false;
        private Material? _satelliteMaterial;

        private readonly LinesVisual3D _wireframeVisual = new() { Color = Color.FromRgb(56, 189, 248), Thickness = 0.5 };
        private readonly Point3DCollection _wireframePoints = new();

        private bool _isProfileModeActive = false;
        private readonly List<Point3D> _profilePoints = new();
        private readonly List<double> _vertexDistances = new();

        private readonly List<System.Windows.Point> _rawProfileData = new();
        private readonly List<Point3D> _rawProfile3DData = new();
        private double _dataMinX = 0, _dataMaxX = 1, _dataMinY = 0, _dataMaxY = 1;
        private double _viewMinX = 0, _viewMaxX = 1, _viewMinY = 0, _viewMaxY = 1;
        private bool _isPanningProfile = false;
        private System.Windows.Point _lastPanMousePos;

        private Line? _tracker2DLine;
        private TextBlock? _tracker2DText;

        public MainWindow()
        {
            InitializeComponent();
            LoadOpenTopoApiKey();

            // Set initial button backgrounds to match initial toggle state
            BtnToggleHeatmap.Background = _isHeatmapActive ? new SolidColorBrush(Color.FromRgb(56, 189, 248)) : new SolidColorBrush(Color.FromRgb(26, 26, 26));
            BtnToggleWireframe.Background = _isWireframeActive ? new SolidColorBrush(Color.FromRgb(56, 189, 248)) : new SolidColorBrush(Color.FromRgb(26, 26, 26));
            BtnToggleSatellite.Background = _isSatelliteActive ? new SolidColorBrush(Color.FromRgb(56, 189, 248)) : new SolidColorBrush(Color.FromRgb(26, 26, 26));
        }

        private void LoadOpenTopoApiKey()
        {
            try
            {
                string keyFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "opentopography_key.txt");
                if (System.IO.File.Exists(keyFilePath))
                {
                    TxtOpenTopoApiKey.Password = System.IO.File.ReadAllText(keyFilePath).Trim();
                }
            }
            catch { }
        }

        private void SaveOpenTopoApiKey(string key)
        {
            try
            {
                string keyFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "opentopography_key.txt");
                System.IO.File.WriteAllText(keyFilePath, key);
            }
            catch { }
        }

        // =========================================================================
        // UNIFIED DATA INGESTION ENGINE (TXT, CSV, XML, DXF)
        // =========================================================================
        private void BtnImportCsv_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new()
            {
                Filter = "Terrain Data (*.csv;*.txt;*.xml;*.dxf)|*.csv;*.txt;*.xml;*.dxf|All Files (*.*)|*.*"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    List<CivilPoint> loadedPoints;
                    string ext = System.IO.Path.GetExtension(openFileDialog.FileName).ToLower();

                    if (ext == ".dxf")
                    {
                        TxtStatus.Text = "Status: Parsing CAD Geometry...";
                        loadedPoints = ExtractPointsFromDxf(openFileDialog.FileName);
                    }
                    else
                    {
                        TxtStatus.Text = "Status: Parsing Text/XML Data...";
                        loadedPoints = SurveyParser.ParseFile(openFileDialog.FileName);
                    }

                    if (loadedPoints.Count > 0)
                    {
                        // Reset satellite status for CSV survey files (no geo coordinates available)
                        _isSatelliteActive = false;
                        _isSatelliteAvailable = false;
                        BtnToggleSatellite.Background = new SolidColorBrush(Color.FromRgb(26, 26, 26));

                        RenderSurveyPoints(loadedPoints);
                        TxtPointCount.Text = $"Points Loaded: {loadedPoints.Count}";
                        TxtStatus.Text = "Status: Surface Mesh Triangulated";
                    }
                    else
                    {
                        MessageBox.Show("No valid 3D points found in this file.", "Parse Error");
                        TxtStatus.Text = "Status: Idle";
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Execution failed: {ex.Message}");
                    TxtStatus.Text = "Status: Error";
                }
            }
        }

        private async void BtnImportKml_Click(object sender, RoutedEventArgs e)
        {
            string apiKey = TxtOpenTopoApiKey.Password.Trim();
            bool isOfflineMode = false;
            if (string.IsNullOrEmpty(apiKey))
            {
                var offlinePrompt = MessageBox.Show(
                    "An OpenTopography API Key is required to fetch real online terrain data.\n\n" +
                    "Would you like to proceed in Offline Mode and load a high-quality synthetic/mock terrain mesh?",
                    "API Key Required / Offline Mode",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (offlinePrompt == MessageBoxResult.Yes)
                {
                    isOfflineMode = true;
                    apiKey = "offline";
                }
                else
                {
                    return;
                }
            }

            OpenFileDialog openFileDialog = new()
            {
                Filter = "KML files (*.kml)|*.kml|All Files (*.*)|*.*"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    // Parse KML polygon to get boundary points (longitude = X, latitude = Y)
                    var boundaryPoints = SurveyParser.ParseKmlPolygon(openFileDialog.FileName);
                    if (boundaryPoints.Count == 0)
                    {
                        MessageBox.Show("No polygon points found in KML file.", "Parse Error");
                        return;
                    }

                    // Initialise the coordinate projection using the center of the boundary
                    double centerLon = (boundaryPoints.Min(p => p.X) + boundaryPoints.Max(p => p.X)) / 2.0;
                    double centerLat = (boundaryPoints.Min(p => p.Y) + boundaryPoints.Max(p => p.Y)) / 2.0;
                    CoordinateProjection.Initialise(centerLat, centerLon);

                    // Project boundary points to local Cartesian meters
                    var localBoundaryPoints = boundaryPoints.Select(p =>
                    {
                        var (localX, localY) = CoordinateProjection.ToLocal(p.Y, p.X);
                        return new CivilPoint(localX, localY, p.Z);
                    }).ToList();

                    // Determine bounding box in geographic degrees for the OpenTopography API
                    double west = boundaryPoints.Min(p => p.X);
                    double east = boundaryPoints.Max(p => p.X);
                    double south = boundaryPoints.Min(p => p.Y);
                    double north = boundaryPoints.Max(p => p.Y);

                    // Determine local metric bounding box for area calculation
                    double minX = localBoundaryPoints.Min(p => p.X);
                    double maxX = localBoundaryPoints.Max(p => p.X);
                    double minY = localBoundaryPoints.Min(p => p.Y);
                    double maxY = localBoundaryPoints.Max(p => p.Y);

                    // Area warning and optional cropping
                    double areaSqKm = ((maxX - minX) / 1000.0) * ((maxY - minY) / 1000.0);
                    if (areaSqKm > 4.0)
                    {
                        var result = MessageBox.Show(
                            $"The selected KML boundary covers {areaSqKm:F1} km², which is very large. " +
                            "To get a smooth, high-quality surface and prevent API rate-limiting, it is highly recommended to restrict the boundary to under 4.0 km².\n\n" +
                            "Would you like to automatically crop the terrain to a high-quality 2.0 km x 2.0 km centered region?\n\n" +
                            "• Select YES to crop to a high-quality 2.0 km x 2.0 km centered region (Recommended).\n" +
                            "• Select NO to proceed with the full size (generates a coarser surface).\n" +
                            "• Select CANCEL to abort.",
                            "Large Boundary Extents",
                            MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Warning);

                        if (result == MessageBoxResult.Cancel) return;
                        if (result == MessageBoxResult.Yes)
                        {
                            // Shift geographical bounds to represent the centered 2.0 km x 2.0 km square
                            double centerLocalX = (minX + maxX) / 2.0;
                            double centerLocalY = (minY + maxY) / 2.0;

                            var (bottomLat, leftLon) = CoordinateProjection.ToGeodetic(centerLocalX - 1000.0, centerLocalY - 1000.0);
                            var (topLat, rightLon) = CoordinateProjection.ToGeodetic(centerLocalX + 1000.0, centerLocalY + 1000.0);

                            west = Math.Min(leftLon, rightLon);
                            east = Math.Max(leftLon, rightLon);
                            south = Math.Min(bottomLat, topLat);
                            north = Math.Max(bottomLat, topLat);
                        }
                    }

                    using var http = new System.Net.Http.HttpClient();
                    string gridContent = string.Empty;

                    if (isOfflineMode)
                    {
                        gridContent = GenerateMockAsciiGrid(west, east, south, north);
                    }
                    else
                    {
                        TxtStatus.Text = "Status: Querying OpenTopography DEM...";
                        string url = $"https://portal.opentopography.org/API/globaldem?demtype=COP30&south={south.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}&north={north.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}&west={west.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}&east={east.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}&outputFormat=AAIGrid&API_Key={apiKey}";

                        try
                        {
                            var response = await http.GetAsync(url);
                            response.EnsureSuccessStatusCode();
                            gridContent = await response.Content.ReadAsStringAsync();
                            SaveOpenTopoApiKey(apiKey);
                        }
                        catch (Exception ex) when (ex is HttpRequestException httpEx && (httpEx.StatusCode == null || httpEx.InnerException is System.Net.Sockets.SocketException || httpEx.Message.Contains("No such host is known")) || ex is System.Net.Sockets.SocketException)
                        {
                            var offlineResult = MessageBox.Show(
                                "Could not connect to the OpenTopography server (host may be unreachable or you are offline).\n\n" +
                                "Would you like to generate and load a high-quality synthetic offline terrain mesh to test the application offline?",
                                "Offline Mode / Connection Failed",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question);

                            if (offlineResult == MessageBoxResult.Yes)
                            {
                                isOfflineMode = true;
                                gridContent = GenerateMockAsciiGrid(west, east, south, north);
                            }
                            else
                            {
                                TxtStatus.Text = "Status: Connection Failed";
                                return;
                            }
                        }
                    }

                    TxtStatus.Text = "Status: Parsing DEM Grid...";

                    // Parse ASCII Grid using the new helper in SurveyParser
                    var localElevated = SurveyParser.ParseAsciiGrid(gridContent, localBoundaryPoints);

                    if (localElevated.Count == 0)
                    {
                        MessageBox.Show("No grid points could be generated within the KML polygon. Please check the polygon dimensions.", "Grid Generation Error");
                        return;
                    }

                    // Dynamically calculate the spacing of the parsed points for Laplacian smoothing
                    double minGridX = localElevated.Min(p => p.X);
                    double maxGridX = localElevated.Max(p => p.X);
                    double minGridY = localElevated.Min(p => p.Y);
                    double maxGridY = localElevated.Max(p => p.Y);
                    double spacing = Math.Max(2.5, Math.Max(maxGridX - minGridX, maxGridY - minGridY) / 50.0);

                    // Apply Laplacian smoothing filter to resolve boxy/staircasing terrain data
                    var smoothedPoints = SmoothElevations(localElevated, spacing);

                    RenderSurveyPoints(smoothedPoints);
                    TxtPointCount.Text = $"Points Loaded: {smoothedPoints.Count}";
                    TxtStatus.Text = "Status: Surface Mesh Triangulated";

                    // Reset satellite UI state
                    _isSatelliteActive = false;
                    _isSatelliteAvailable = false;
                    BtnToggleSatellite.Background = new SolidColorBrush(Color.FromRgb(26, 26, 26));

                    // Asynchronously fetch ArcGIS satellite imagery for this bounding box
                    try
                    {
                        if (isOfflineMode)
                        {
                            TxtStatus.Text = "Status: Generating Offline Satellite Overlay...";
                            string satImgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "satellite_temp.jpg");
                            CreateMockSatelliteImage(satImgPath);
                            _isSatelliteAvailable = true;
                            TxtStatus.Text = "Status: Terrain & Offline Satellite Loaded";
                        }
                        else
                        {
                            TxtStatus.Text = "Status: Downloading Satellite Overlay...";
                            string satUrl = $"https://services.arcgisonline.com/arcgis/rest/services/World_Imagery/MapServer/export?bbox={west.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)},{south.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)},{east.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)},{north.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}&bboxSR=4326&size=1024,1024&format=jpg&f=image";

                            byte[] imgBytes = await http.GetByteArrayAsync(satUrl);
                            string satImgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "satellite_temp.jpg");
                            System.IO.File.WriteAllBytes(satImgPath, imgBytes);
                            _isSatelliteAvailable = true;
                            TxtStatus.Text = "Status: Terrain & Satellite Loaded";
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            string satImgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "satellite_temp.jpg");
                            CreateMockSatelliteImage(satImgPath);
                            _isSatelliteAvailable = true;
                            TxtStatus.Text = "Status: Terrain Loaded (Offline satellite fallback)";
                        }
                        catch
                        {
                            _isSatelliteAvailable = false;
                            TxtStatus.Text = "Status: Terrain Loaded (Satellite failed)";
                        }
                        System.Diagnostics.Debug.WriteLine($"Failed to download satellite imagery: {ex.Message}");
                    }
                }
                catch (System.Net.Http.HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException || ex.Message.Contains("No such host is known"))
                {
                    MessageBox.Show("Could not resolve the OpenTopography server. Please verify your internet connection or check if a VPN/proxy is blocking the request, then try again.", "Network Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    TxtStatus.Text = "Status: Connection Failed";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Execution failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    TxtStatus.Text = "Status: Error";
                }
            }
        }

        // Simple ray‑casting point‑in‑polygon implementation
        private bool IsPointInPolygon(System.Windows.Point pt, List<System.Windows.Point> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var pi = polygon[i];
                var pj = polygon[j];
                bool intersect = ((pi.Y > pt.Y) != (pj.Y > pt.Y)) &&
                                 (pt.X < (pj.X - pi.X) * (pt.Y - pi.Y) / (pj.Y - pi.Y) + pi.X);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static List<CivilPoint> ExtractPointsFromDxf(string filePath)
        {
            List<CivilPoint> points = new();
            DxfDocument? dxf = null;

            try
            {
                dxf = DxfDocument.Load(filePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to parse CAD file. Please open the file in AutoCAD and use 'Save As -> AutoCAD 2013 or 2018 DXF'.\n\nLibrary Error: {ex.Message}", "DXF Version Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return points;
            }

            if (dxf == null) return points;

            if (dxf.Entities.Polylines2D != null)
            {
                foreach (var poly in dxf.Entities.Polylines2D)
                {
                    double z = poly.Elevation;
                    foreach (var v in poly.Vertexes)
                        points.Add(new CivilPoint(v.Position.X, v.Position.Y, z));
                }
            }

            if (dxf.Entities.Polylines3D != null)
            {
                foreach (var poly in dxf.Entities.Polylines3D)
                {
                    foreach (var v in poly.Vertexes)
                        points.Add(new CivilPoint(v.X, v.Y, v.Z));
                }
            }

            if (dxf.Entities.Lines != null)
            {
                foreach (var line in dxf.Entities.Lines)
                {
                    points.Add(new CivilPoint(line.StartPoint.X, line.StartPoint.Y, line.StartPoint.Z));
                    points.Add(new CivilPoint(line.EndPoint.X, line.EndPoint.Y, line.EndPoint.Z));
                }
            }

            if (dxf.Entities.Points != null)
            {
                foreach (var pt in dxf.Entities.Points)
                {
                    points.Add(new CivilPoint(pt.Position.X, pt.Position.Y, pt.Position.Z));
                }
            }

            return points;
        }

        private void RenderSurveyPoints(List<CivilPoint> points)
        {
            TerrainModelContainer.Children.Clear();
            MinorContoursVisual.Points.Clear();
            MajorContoursVisual.Points.Clear();
            ProfileLineVisual.Points.Clear();
            ProfileSlider.Visibility = Visibility.Collapsed;
            ProfileTracker3D.Radius = 0;
            _localTriangles.Clear();
            _contourLines.Clear();
            _wireframePoints.Clear();

            if (points == null || points.Count == 0)
            {
                TxtMaxElev.Text = "0.00 m";
                TxtMidElev.Text = "0.00 m";
                TxtMinElev.Text = "0.00 m";
                TxtPointCount.Text = "Points Loaded: 0";
                return;
            }

            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue;

            foreach (var p in points)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
            }

            _globalCenterX = (minX + maxX) / 2.0;
            _globalCenterY = (minY + maxY) / 2.0;
            _globalBaseZ = minZ;

            DelaunayTriangulationBuilder triangulationBuilder = new();

            List<Coordinate> coordinates = points.Select(pt =>
                (Coordinate)new CoordinateZ(pt.X - _globalCenterX, pt.Y - _globalCenterY, pt.Z - _globalBaseZ)
            ).ToList();

            // Deduplicate coordinates on X-Y plane to prevent DelaunayTriangulationBuilder locate failure.
            // Using a dynamic tolerance based on the extents of the boundary to clean up coincident or very close points.
            double spanX = maxX - minX;
            double spanY = maxY - minY;
            double maxSpan = Math.Max(spanX, spanY);
            double tolerance = Math.Max(1e-4, maxSpan * 1e-7);

            List<Coordinate> uniqueCoordinates = new();
            HashSet<(long, long)> seenGrid = new();
            foreach (var coord in coordinates)
            {
                long gx = (long)Math.Round(coord.X / tolerance);
                long gy = (long)Math.Round(coord.Y / tolerance);
                if (seenGrid.Add((gx, gy)))
                {
                    uniqueCoordinates.Add(coord);
                }
            }

            triangulationBuilder.SetSites(uniqueCoordinates);
            var triangleGeometries = triangulationBuilder.GetTriangles(new GeometryFactory());
            HelixToolkit.Geometry.MeshBuilder meshBuilder = new();

            foreach (var geometry in triangleGeometries)
            {
                if (geometry is NetTopologySuite.Geometries.Polygon poly)
                {
                    var coords = poly.Coordinates;

                    System.Numerics.Vector3 p0 = new((float)coords[0].X, (float)coords[0].Y, (float)coords[0].Z);
                    System.Numerics.Vector3 p1 = new((float)coords[1].X, (float)coords[1].Y, (float)coords[1].Z);
                    System.Numerics.Vector3 p2 = new((float)coords[2].X, (float)coords[2].Y, (float)coords[2].Z);

                    meshBuilder.AddTriangle(p0, p1, p2);
                    _localTriangles.Add(new[] { p0, p1, p2 });
                }
            }

            var rawMesh = meshBuilder.ToMesh();
            MeshGeometry3D wpfMesh = new();

            double localMaxZ = 0;
            if (rawMesh.Positions != null && rawMesh.Positions.Count > 0)
            {
                localMaxZ = rawMesh.Positions.Max(p => p.Z);

                foreach (var p in rawMesh.Positions)
                {
                    wpfMesh.Positions.Add(new Point3D(p.X, p.Y, p.Z));
                    double u = localMaxZ == 0 ? 0 : (p.Z / localMaxZ);
                    wpfMesh.TextureCoordinates.Add(new System.Windows.Point(u, 0.5));
                }
            }

            if (rawMesh.TriangleIndices != null)
                foreach (var t in rawMesh.TriangleIndices) wpfMesh.TriangleIndices.Add(t);

            _solidMaterial = MaterialHelper.CreateMaterial(Color.FromRgb(46, 139, 87));

            LinearGradientBrush heatBrush = new() { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
            heatBrush.GradientStops.Add(new GradientStop(Colors.Blue, 0.0));
            heatBrush.GradientStops.Add(new GradientStop(Colors.Cyan, 0.25));
            heatBrush.GradientStops.Add(new GradientStop(Colors.LimeGreen, 0.5));
            heatBrush.GradientStops.Add(new GradientStop(Colors.Yellow, 0.75));
            heatBrush.GradientStops.Add(new GradientStop(Colors.Red, 1.0));
            _heatMaterial = new DiffuseMaterial(heatBrush);

            _invisibleHitTestMaterial ??= MaterialHelper.CreateMaterial(Color.FromArgb(1, 0, 0, 0));
            var backMaterial = MaterialHelper.CreateMaterial(Color.FromRgb(40, 40, 40));

            if (rawMesh.TriangleIndices != null && rawMesh.Positions != null)
            {
                for (int i = 0; i < rawMesh.TriangleIndices.Count; i += 3)
                {
                    Point3D p0 = wpfMesh.Positions[rawMesh.TriangleIndices[i]];
                    Point3D p1 = wpfMesh.Positions[rawMesh.TriangleIndices[i + 1]];
                    Point3D p2 = wpfMesh.Positions[rawMesh.TriangleIndices[i + 2]];

                    _wireframePoints.Add(p0); _wireframePoints.Add(p1);
                    _wireframePoints.Add(p1); _wireframePoints.Add(p2);
                    _wireframePoints.Add(p2); _wireframePoints.Add(p0);
                }
            }
            _wireframeVisual.Points = _wireframePoints;

            Material? activeMaterial = _isWireframeActive ? _invisibleHitTestMaterial : (_isHeatmapActive ? _heatMaterial : _solidMaterial);
            Material? activeBackMaterial = _isWireframeActive ? _invisibleHitTestMaterial : backMaterial;

            _terrainModel = new GeometryModel3D
            {
                Geometry = wpfMesh,
                Material = activeMaterial,
                BackMaterial = activeBackMaterial
            };

            Model3DGroup meshGroup = new();
            meshGroup.Children.Add(_terrainModel);
            TerrainModelContainer.Content = meshGroup;

            if (_isWireframeActive && !Viewport.Children.Contains(_wireframeVisual))
            {
                Viewport.Children.Add(_wireframeVisual);
            }

            TxtMaxElev.Text = $"{_globalBaseZ + localMaxZ:F2} m";
            TxtMidElev.Text = $"{_globalBaseZ + (localMaxZ / 2):F2} m";
            TxtMinElev.Text = $"{_globalBaseZ:F2} m";

            ElevationLegend.Visibility = (_isHeatmapActive && !_isWireframeActive) ? Visibility.Visible : Visibility.Collapsed;

            Viewport.ZoomExtents(500);
        }

        private void BtnToggleWireframe_Click(object sender, RoutedEventArgs e)
        {
            _isWireframeActive = !_isWireframeActive;
            BtnToggleWireframe.Background = _isWireframeActive ? new SolidColorBrush(Color.FromRgb(56, 189, 248)) : new SolidColorBrush(Color.FromRgb(26, 26, 26));

            if (_terrainModel == null || _invisibleHitTestMaterial == null) return;

            if (_isWireframeActive)
            {
                _terrainModel.Material = _invisibleHitTestMaterial;
                _terrainModel.BackMaterial = _invisibleHitTestMaterial;
                ElevationLegend.Visibility = Visibility.Collapsed;

                if (!Viewport.Children.Contains(_wireframeVisual)) Viewport.Children.Add(_wireframeVisual);
            }
            else
            {
                _terrainModel.Material = _isSatelliteActive ? _satelliteMaterial : (_isHeatmapActive ? _heatMaterial : _solidMaterial);
                _terrainModel.BackMaterial = MaterialHelper.CreateMaterial(Color.FromRgb(40, 40, 40));
                ElevationLegend.Visibility = (_isHeatmapActive && !_isSatelliteActive) ? Visibility.Visible : Visibility.Collapsed;

                if (Viewport.Children.Contains(_wireframeVisual)) Viewport.Children.Remove(_wireframeVisual);
            }
        }

        private void BtnToggleHeatmap_Click(object sender, RoutedEventArgs e)
        {
            _isHeatmapActive = !_isHeatmapActive;
            BtnToggleHeatmap.Background = _isHeatmapActive ? new SolidColorBrush(Color.FromRgb(56, 189, 248)) : new SolidColorBrush(Color.FromRgb(26, 26, 26));

            if (_isSatelliteActive && _isHeatmapActive)
            {
                _isSatelliteActive = false;
                BtnToggleSatellite.Background = new SolidColorBrush(Color.FromRgb(26, 26, 26));
            }

            if (_terrainModel == null || _solidMaterial == null || _heatMaterial == null) return;
            var wpfMesh = _terrainModel.Geometry as MeshGeometry3D;
            if (wpfMesh == null) return;

            if (!_isSatelliteActive)
            {
                double maxZ = wpfMesh.Positions.Max(pt => pt.Z);
                wpfMesh.TextureCoordinates.Clear();
                foreach (var pt in wpfMesh.Positions)
                {
                    double u = maxZ == 0 ? 0 : (pt.Z / maxZ);
                    wpfMesh.TextureCoordinates.Add(new System.Windows.Point(u, 0.5));
                }

                if (!_isWireframeActive)
                {
                    _terrainModel.Material = _isHeatmapActive ? _heatMaterial : _solidMaterial;
                    ElevationLegend.Visibility = _isHeatmapActive ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private void BtnToggleSatellite_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSatelliteAvailable)
            {
                MessageBox.Show("Satellite imagery is not loaded. Draping is only available when a KML boundary is imported successfully with an active internet connection.", "Satellite Image Unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_terrainModel == null) return;
            var wpfMesh = _terrainModel.Geometry as MeshGeometry3D;
            if (wpfMesh == null) return;

            _isSatelliteActive = !_isSatelliteActive;
            BtnToggleSatellite.Background = _isSatelliteActive ? new SolidColorBrush(Color.FromRgb(56, 189, 248)) : new SolidColorBrush(Color.FromRgb(26, 26, 26));

            if (_isSatelliteActive)
            {
                // Deactivate heatmap if active to avoid overlay conflicts
                if (_isHeatmapActive)
                {
                    _isHeatmapActive = false;
                    BtnToggleHeatmap.Background = new SolidColorBrush(Color.FromRgb(26, 26, 26));
                    ElevationLegend.Visibility = Visibility.Collapsed;
                }

                // Calculate local metric bounding box for spatial UV mapping
                double minX = wpfMesh.Positions.Min(pt => pt.X);
                double maxX = wpfMesh.Positions.Max(pt => pt.X);
                double minY = wpfMesh.Positions.Min(pt => pt.Y);
                double maxY = wpfMesh.Positions.Max(pt => pt.Y);
                double dx = maxX - minX;
                double dy = maxY - minY;

                wpfMesh.TextureCoordinates.Clear();
                foreach (var pt in wpfMesh.Positions)
                {
                    double u = dx == 0 ? 0 : (pt.X - minX) / dx;
                    double v = dy == 0 ? 0 : 1.0 - (pt.Y - minY) / dy;
                    wpfMesh.TextureCoordinates.Add(new System.Windows.Point(u, v));
                }

                // Load satellite texture material
                string satImgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "satellite_temp.jpg");
                if (System.IO.File.Exists(satImgPath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(satImgPath);
                    bitmap.EndInit();

                    var brush = new ImageBrush(bitmap);
                    _satelliteMaterial = new DiffuseMaterial(brush);
                }

                if (!_isWireframeActive)
                {
                    _terrainModel.Material = _satelliteMaterial;
                }
            }
            else
            {
                // Restore elevation (height-based) texture coordinates
                double maxZ = wpfMesh.Positions.Max(pt => pt.Z);
                wpfMesh.TextureCoordinates.Clear();
                foreach (var pt in wpfMesh.Positions)
                {
                    double u = maxZ == 0 ? 0 : (pt.Z / maxZ);
                    wpfMesh.TextureCoordinates.Add(new System.Windows.Point(u, 0.5));
                }

                if (!_isWireframeActive)
                {
                    _terrainModel.Material = _isHeatmapActive ? _heatMaterial : _solidMaterial;
                }
            }
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (_terrainModel == null) return;

            System.Windows.Point mousePos = e.GetPosition(Viewport);
            HitTestResult hitResult = VisualTreeHelper.HitTest(Viewport, mousePos);

            if (hitResult is RayMeshGeometry3DHitTestResult meshHit && meshHit.ModelHit == _terrainModel)
            {
                Point3D p = meshHit.PointHit;
                double realX = p.X + _globalCenterX;
                double realY = p.Y + _globalCenterY;
                double realZ = p.Z + _globalBaseZ;

                TxtStaticZ.Text = $"{realZ:F3} m";
                TxtStaticE.Text = $"{realX:F3}";
                TxtStaticN.Text = $"{realY:F3}";
            }
            else
            {
                TxtStaticZ.Text = "---";
                TxtStaticE.Text = "---";
                TxtStaticN.Text = "---";
            }
        }

        private void Viewport_MouseLeave(object sender, MouseEventArgs e)
        {
            TxtStaticZ.Text = "---";
            TxtStaticE.Text = "---";
            TxtStaticN.Text = "---";
        }

        // =========================================================================
        // 2D CROSS-SECTION PROFILE ENGINE (WITH PAN & ZOOM)
        // =========================================================================
        private void BtnProfileTool_Click(object sender, RoutedEventArgs e)
        {
            _isProfileModeActive = !_isProfileModeActive;
            BtnProfileTool.Background = _isProfileModeActive ? new SolidColorBrush(Color.FromRgb(239, 68, 68)) : new SolidColorBrush(Color.FromRgb(26, 26, 26));

            if (_isProfileModeActive)
            {
                _profilePoints.Clear();
                _vertexDistances.Clear();
                ProfileLineVisual.Points.Clear();
                ProfileSlider.Visibility = Visibility.Collapsed;
                ProfileTracker3D.Radius = 0;
                BtnProfileZoomAll.Visibility = Visibility.Collapsed;
                TxtProfileStatus.Text = "📈 CLICK POINTS ON MESH (DOUBLE-CLICK / RIGHT-CLICK OR TOGGLE RULER TO FINISH)...";
                TxtProfileStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                ProfileEditorPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                TxtProfileStatus.Text = "📈 VERTICAL PROFILE ALIGNMENT";
                TxtProfileStatus.Foreground = new SolidColorBrush(Color.FromRgb(209, 213, 219));
                if (_profilePoints.Count >= 2)
                {
                    Generate2DProfile();
                    PopulateProfileVerticesEditor();
                }
            }
        }

        private void Viewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isProfileModeActive || _terrainModel == null) return;
            e.Handled = true;

            System.Windows.Point mousePos = e.GetPosition(Viewport);
            HitTestResult hitResult = VisualTreeHelper.HitTest(Viewport, mousePos);

            if (hitResult is RayMeshGeometry3DHitTestResult meshHit && meshHit.ModelHit == _terrainModel)
            {
                Point3D hitPt = meshHit.PointHit;
                if (e.ClickCount > 1)
                {
                    if (_profilePoints.Count == 0 || Point3D.Subtract(_profilePoints.Last(), hitPt).Length > 0.1)
                    {
                        _profilePoints.Add(hitPt);
                    }
                    FinishDrawingProfile();
                }
                else
                {
                    _profilePoints.Add(hitPt);
                    Update3DProfilePreview();
                    TxtProfileStatus.Text = $"📈 VERTICES: {_profilePoints.Count}. CLICK TO ADD NEXT POINT...";
                }
            }
        }

        private void Viewport_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isProfileModeActive || _terrainModel == null) return;
            e.Handled = true;
            FinishDrawingProfile();
        }

        private void Update3DProfilePreview()
        {
            Point3DCollection previewPoints = new();
            for (int i = 0; i < _profilePoints.Count - 1; i++)
            {
                previewPoints.Add(_profilePoints[i]);
                previewPoints.Add(_profilePoints[i + 1]);
            }
            ProfileLineVisual.Points = previewPoints;
        }

        private void FinishDrawingProfile()
        {
            _isProfileModeActive = false;
            BtnProfileTool.Background = new SolidColorBrush(Color.FromRgb(26, 26, 26));
            TxtProfileStatus.Text = "📈 VERTICAL PROFILE ALIGNMENT";
            TxtProfileStatus.Foreground = new SolidColorBrush(Color.FromRgb(209, 213, 219));

            if (_profilePoints.Count >= 2)
            {
                Generate2DProfile();
                PopulateProfileVerticesEditor();
            }
            else
            {
                MessageBox.Show("Please click at least 2 points to generate a vertical profile.", "Profile Generation", MessageBoxButton.OK, MessageBoxImage.Warning);
                ProfileLineVisual.Points.Clear();
            }
        }

        private void Generate2DProfile()
        {
            if (_profilePoints.Count < 2) return;

            double currentSliderRatio = 0.5;
            if (ProfileSlider != null && ProfileSlider.Visibility == Visibility.Visible && ProfileSlider.Maximum > 0)
            {
                currentSliderRatio = ProfileSlider.Value / ProfileSlider.Maximum;
            }

            _rawProfileData.Clear();
            _rawProfile3DData.Clear();
            _vertexDistances.Clear();

            // Calculate vertex distances
            double totalDistance = 0;
            _vertexDistances.Add(0);
            for (int i = 0; i < _profilePoints.Count - 1; i++)
            {
                var p1 = _profilePoints[i];
                var p2 = _profilePoints[i + 1];
                double dist = Math.Sqrt((p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y));
                totalDistance += dist;
                _vertexDistances.Add(totalDistance);
            }

            if (totalDistance == 0) return;

            int samples = 300;
            double stepDist = totalDistance / samples;

            double minZ = double.MaxValue;
            double maxZ = double.MinValue;

            Point3DCollection new3DLinePoints = new();
            Point3D? last3DPt = null;

            for (int i = 0; i <= samples; i++)
            {
                double d = (double)i / samples * totalDistance;
                Point3D pt = GetPointAtDistance(d);

                double? pz = GetElevationAt(pt.X, pt.Y);
                if (pz != null)
                {
                    double realZ = pz.Value + _globalBaseZ;
                    _rawProfileData.Add(new System.Windows.Point(d, realZ));

                    if (realZ < minZ) minZ = realZ;
                    if (realZ > maxZ) maxZ = realZ;

                    Point3D current3DPt = new Point3D(pt.X, pt.Y, pz.Value + 1.0);
                    _rawProfile3DData.Add(current3DPt);

                    if (last3DPt != null)
                    {
                        new3DLinePoints.Add(last3DPt.Value);
                        new3DLinePoints.Add(current3DPt);
                    }
                    last3DPt = current3DPt;
                }
            }

            ProfileLineVisual.Points = new3DLinePoints;

            if (_rawProfileData.Count < 2) return;

            _dataMinX = 0;
            _dataMaxX = _rawProfileData.Last().X;
            double zPadding = (maxZ - minZ) * 0.1;
            if (zPadding == 0) zPadding = 5;
            _dataMinY = minZ - zPadding;
            _dataMaxY = maxZ + zPadding;

            BtnProfileZoomAll_Click(null!, null!);

            if (ProfileSlider != null)
            {
                ProfileSlider.Maximum = _rawProfile3DData.Count - 1;
                ProfileSlider.Value = currentSliderRatio * ProfileSlider.Maximum;
                ProfileSlider.Visibility = Visibility.Visible;
            }
            if (BtnProfileZoomAll != null)
            {
                BtnProfileZoomAll.Visibility = Visibility.Visible;
            }
        }

        private Point3D GetPointAtDistance(double distance)
        {
            if (_profilePoints.Count == 0) return new Point3D();
            if (_profilePoints.Count == 1 || distance <= 0) return _profilePoints[0];

            double accumulated = 0;
            for (int i = 0; i < _profilePoints.Count - 1; i++)
            {
                var p1 = _profilePoints[i];
                var p2 = _profilePoints[i + 1];
                double segLen = Math.Sqrt((p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y));
                if (accumulated + segLen >= distance)
                {
                    double ratio = (distance - accumulated) / segLen;
                    double x = p1.X + (p2.X - p1.X) * ratio;
                    double y = p1.Y + (p2.Y - p1.Y) * ratio;
                    double z = p1.Z + (p2.Z - p1.Z) * ratio;
                    return new Point3D(x, y, z);
                }
                accumulated += segLen;
            }
            return _profilePoints.Last();
        }

        private double MapX(double x, double width, double padX)
        {
            return padX + (x - _viewMinX) * (width - 2 * padX) / (_viewMaxX - _viewMinX);
        }

        private double MapY(double y, double height, double padY)
        {
            return height - padY - (y - _viewMinY) * (height - 2 * padY) / (_viewMaxY - _viewMinY);
        }

        private void RedrawProfileCanvas()
        {
            ProfileCanvas.Children.Clear();
            if (_rawProfileData.Count == 0) return;

            double width = ProfileCanvasWrapper.ActualWidth > 0 ? ProfileCanvasWrapper.ActualWidth : 1000;
            double height = ProfileCanvasWrapper.ActualHeight > 0 ? ProfileCanvasWrapper.ActualHeight : 170;
            double padX = 60;
            double padY = 30;

            int gridSteps = 5;

            double xStep = (_viewMaxX - _viewMinX) / gridSteps;
            for (int i = 0; i <= gridSteps; i++)
            {
                double valX = _viewMinX + (i * xStep);
                double screenX = MapX(valX, width, padX);

                Line vertLine = new() { X1 = screenX, Y1 = padY, X2 = screenX, Y2 = height - padY, Stroke = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), StrokeThickness = 1 };
                ProfileCanvas.Children.Add(vertLine);

                TextBlock txt = new() { Text = $"{valX:F0}m", Foreground = Brushes.Gray, FontSize = 10, FontFamily = new FontFamily("Consolas") };
                Canvas.SetLeft(txt, screenX - 10); Canvas.SetTop(txt, height - padY + 5);
                ProfileCanvas.Children.Add(txt);
            }

            double yStep = (_viewMaxY - _viewMinY) / gridSteps;
            for (int i = 0; i <= gridSteps; i++)
            {
                double valY = _viewMinY + (i * yStep);
                double screenY = MapY(valY, height, padY);

                Line horizLine = new() { X1 = padX, Y1 = screenY, X2 = width - padX, Y2 = screenY, Stroke = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), StrokeThickness = 1 };
                ProfileCanvas.Children.Add(horizLine);

                TextBlock txt = new() { Text = $"{valY:F1}", Foreground = Brushes.Gray, FontSize = 10, FontFamily = new FontFamily("Consolas") };
                Canvas.SetLeft(txt, 5); Canvas.SetTop(txt, screenY - 6);
                ProfileCanvas.Children.Add(txt);
            }

            Polyline curve = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(56, 189, 248)), StrokeThickness = 2 };
            System.Windows.Shapes.Polygon fill = new System.Windows.Shapes.Polygon { Fill = new SolidColorBrush(Color.FromArgb(40, 56, 189, 248)) };

            double baselineY = height - padY;
            fill.Points.Add(new System.Windows.Point(MapX(_rawProfileData.First().X, width, padX), baselineY));

            foreach (var p in _rawProfileData)
            {
                double screenX = MapX(p.X, width, padX);
                double screenY = MapY(p.Y, height, padY);

                System.Windows.Point screenPt = new(screenX, screenY);
                curve.Points.Add(screenPt);
                fill.Points.Add(screenPt);
            }

            fill.Points.Add(new System.Windows.Point(MapX(_rawProfileData.Last().X, width, padX), baselineY));

            ProfileCanvas.Children.Add(fill);
            ProfileCanvas.Children.Add(curve);

            // Draw Polyline Vertices and Labels on the Canvas
            for (int i = 0; i < _profilePoints.Count; i++)
            {
                if (i >= _vertexDistances.Count) break;
                double dist = _vertexDistances[i];
                double screenX = MapX(dist, width, padX);

                if (screenX < padX || screenX > width - padX) continue;

                var closestPt = _rawProfileData.OrderBy(p => Math.Abs(p.X - dist)).FirstOrDefault();
                double screenY = MapY(closestPt.Y, height, padY);

                // Dashed vertical line
                Line vertLine = new()
                {
                    X1 = screenX,
                    Y1 = padY,
                    X2 = screenX,
                    Y2 = height - padY,
                    Stroke = Brushes.OrangeRed,
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                };
                ProfileCanvas.Children.Add(vertLine);

                // Circle marker
                Ellipse circle = new()
                {
                    Width = 8,
                    Height = 8,
                    Fill = Brushes.OrangeRed,
                    Stroke = Brushes.White,
                    StrokeThickness = 1.5,
                    Margin = new Thickness(-4, -4, 0, 0)
                };
                Canvas.SetLeft(circle, screenX);
                Canvas.SetTop(circle, screenY);
                ProfileCanvas.Children.Add(circle);

                // Vertex label above marker
                TextBlock label = new()
                {
                    Text = $"V{i + 1}\n{dist:F1}m\n{closestPt.Y:F2}m",
                    Foreground = Brushes.LightGoldenrodYellow,
                    FontSize = 9,
                    FontFamily = new FontFamily("Consolas"),
                    FontWeight = FontWeights.Bold,
                    Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                    Padding = new Thickness(2),
                    TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(label, screenX - 25);
                Canvas.SetTop(label, Math.Max(padY, screenY - 45));
                ProfileCanvas.Children.Add(label);
            }

            System.Windows.Shapes.Rectangle boundary = new() { Width = width - 2 * padX, Height = height - 2 * padY, Stroke = Brushes.Gray, StrokeThickness = 1 };
            Canvas.SetLeft(boundary, padX); Canvas.SetTop(boundary, padY);
            ProfileCanvas.Children.Add(boundary);

            _tracker2DLine = new Line { Y1 = padY, Y2 = height - padY, Stroke = Brushes.Yellow, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 4 }, Visibility = Visibility.Collapsed };
            ProfileCanvas.Children.Add(_tracker2DLine);

            _tracker2DText = new TextBlock { Foreground = Brushes.Yellow, FontSize = 11, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), Padding = new Thickness(4), Visibility = Visibility.Collapsed };
            Canvas.SetTop(_tracker2DText, padY);
            ProfileCanvas.Children.Add(_tracker2DText);

            if (ProfileSlider.Visibility == Visibility.Visible)
            {
                ProfileSlider_ValueChanged(null!, null!);
            }
        }

        private void ProfileCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_rawProfileData.Count == 0) return;

            double zoomFactor = e.Delta > 0 ? 0.8 : 1.25;

            System.Windows.Point mousePos = e.GetPosition(ProfileCanvasWrapper);
            double padX = 60;
            double padY = 30;
            double width = ProfileCanvasWrapper.ActualWidth;
            double height = ProfileCanvasWrapper.ActualHeight;

            if (mousePos.X < padX || mousePos.X > width - padX || mousePos.Y < padY || mousePos.Y > height - padY) return;

            double dataX = _viewMinX + (mousePos.X - padX) * (_viewMaxX - _viewMinX) / (width - 2 * padX);
            double dataY = _viewMaxY - (mousePos.Y - padY) * (_viewMaxY - _viewMinY) / (height - 2 * padY);

            double newWidth = (_viewMaxX - _viewMinX) * zoomFactor;
            double newHeight = (_viewMaxY - _viewMinY) * zoomFactor;

            double ratioX = (mousePos.X - padX) / (width - 2 * padX);
            double ratioY = 1.0 - ((mousePos.Y - padY) / (height - 2 * padY));

            _viewMinX = dataX - newWidth * ratioX;
            _viewMaxX = _viewMinX + newWidth;

            _viewMinY = dataY - newHeight * ratioY;
            _viewMaxY = _viewMinY + newHeight;

            RedrawProfileCanvas();
        }

        private void ProfileCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_rawProfileData.Count == 0) return;
            _isPanningProfile = true;
            _lastPanMousePos = e.GetPosition(ProfileCanvasWrapper);
            ProfileCanvasWrapper.CaptureMouse();
        }

        private void ProfileCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isPanningProfile = false;
            ProfileCanvasWrapper.ReleaseMouseCapture();
        }

        private void ProfileCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanningProfile) return;

            System.Windows.Point currentPos = e.GetPosition(ProfileCanvasWrapper);
            double dxScreen = currentPos.X - _lastPanMousePos.X;
            double dyScreen = currentPos.Y - _lastPanMousePos.Y;

            double width = ProfileCanvasWrapper.ActualWidth;
            double height = ProfileCanvasWrapper.ActualHeight;
            double padX = 60;
            double padY = 30;

            double dxData = dxScreen * (_viewMaxX - _viewMinX) / (width - 2 * padX);
            double dyData = dyScreen * (_viewMaxY - _viewMinY) / (height - 2 * padY);

            _viewMinX -= dxData;
            _viewMaxX -= dxData;
            _viewMinY += dyData;
            _viewMaxY += dyData;

            _lastPanMousePos = currentPos;
            RedrawProfileCanvas();
        }

        private void BtnProfileZoomAll_Click(object sender, RoutedEventArgs e)
        {
            _viewMinX = _dataMinX;
            _viewMaxX = _dataMaxX;
            _viewMinY = _dataMinY;
            _viewMaxY = _dataMaxY;
            RedrawProfileCanvas();
        }

        private void ProfileSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double>? e)
        {
            if (_rawProfile3DData == null || _rawProfileData == null) return;
            if (_rawProfile3DData.Count == 0 || _rawProfileData.Count == 0) return;

            int index = (int)Math.Round(ProfileSlider.Value);
            if (index < 0) index = 0;
            if (index >= _rawProfile3DData.Count) index = _rawProfile3DData.Count - 1;

            var pt3D = _rawProfile3DData[index];
            var dataRAW = _rawProfileData[index];

            ProfileTracker3D.Center = pt3D;
            ProfileTracker3D.Radius = 3.0;

            if (_tracker2DLine != null && _tracker2DText != null)
            {
                double width = ProfileCanvasWrapper.ActualWidth > 0 ? ProfileCanvasWrapper.ActualWidth : 1000;
                double screenX = MapX(dataRAW.X, width, 60);

                if (screenX < 60 || screenX > width - 60)
                {
                    _tracker2DLine.Visibility = Visibility.Collapsed;
                    _tracker2DText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _tracker2DLine.X1 = screenX;
                    _tracker2DLine.X2 = screenX;
                    _tracker2DLine.Visibility = Visibility.Visible;

                    _tracker2DText.Text = $"ELEV: {dataRAW.Y:F2} m\nDIST: {dataRAW.X:F1} m";
                    Canvas.SetLeft(_tracker2DText, screenX + 8);
                    _tracker2DText.Visibility = Visibility.Visible;
                }
            }
        }

        private double? GetElevationAt(double x, double y)
        {
            foreach (var tri in _localTriangles)
            {
                double d1 = (x - tri[2].X) * (tri[0].Y - tri[2].Y) - (tri[0].X - tri[2].X) * (y - tri[2].Y);
                double d2 = (x - tri[0].X) * (tri[1].Y - tri[0].Y) - (tri[1].X - tri[0].X) * (y - tri[0].Y);
                double d3 = (x - tri[1].X) * (tri[2].Y - tri[1].Y) - (tri[2].X - tri[1].X) * (y - tri[1].Y);

                bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
                bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);

                if (!(hasNeg && hasPos))
                {
                    var v0 = tri[0]; var v1 = tri[1]; var v2 = tri[2];
                    var normal = System.Numerics.Vector3.Cross(v1 - v0, v2 - v0);

                    if (normal.Z != 0)
                    {
                        double d = -(normal.X * v0.X + normal.Y * v0.Y + normal.Z * v0.Z);
                        return -(normal.X * x + normal.Y * y + d) / normal.Z;
                    }
                }
            }
            return null;
        }

        // =========================================================================
        // LIGHTNING-FAST MULTI-THREADED CONTOUR ENGINE
        // =========================================================================
        private async void BtnGenerateContours_Click(object sender, RoutedEventArgs e)
        {
            if (_localTriangles.Count == 0) return;

            TxtStatus.Text = "Status: Calculating Contours...";
            BtnGenerateContours.IsEnabled = false;

            if (!double.TryParse(TxtMinorInterval.Text, out double minorInterval)) minorInterval = 1.0;
            if (!double.TryParse(TxtMajorInterval.Text, out double majorInterval)) majorInterval = 5.0;

            var concurrentContours = new System.Collections.Concurrent.ConcurrentBag<ContourSegment>();
            var majorPointsBag = new System.Collections.Concurrent.ConcurrentBag<Point3D>();
            var minorPointsBag = new System.Collections.Concurrent.ConcurrentBag<Point3D>();

            await System.Threading.Tasks.Task.Run(() =>
            {
                System.Threading.Tasks.Parallel.ForEach(_localTriangles, tri =>
                {
                    double tMinZ = Math.Min(tri[0].Z, Math.Min(tri[1].Z, tri[2].Z));
                    double tMaxZ = Math.Max(tri[0].Z, Math.Max(tri[1].Z, tri[2].Z));

                    double startZ = Math.Ceiling(tMinZ / minorInterval) * minorInterval;

                    for (double currentZ = startZ; currentZ <= tMaxZ; currentZ += minorInterval)
                    {
                        double realWorldZ = currentZ + _globalBaseZ;
                        double remainder = Math.Abs((realWorldZ / majorInterval) - Math.Round(realWorldZ / majorInterval)) * majorInterval;
                        bool isMajor = remainder < 0.001;

                        List<Point3D> intersectPoints = new();
                        CheckTriangleEdgeIntersect(tri[0], tri[1], currentZ, intersectPoints);
                        CheckTriangleEdgeIntersect(tri[1], tri[2], currentZ, intersectPoints);
                        CheckTriangleEdgeIntersect(tri[2], tri[0], currentZ, intersectPoints);

                        if (intersectPoints.Count >= 2)
                        {
                            var p1 = intersectPoints[0];
                            var p2 = intersectPoints[1];

                            concurrentContours.Add(new ContourSegment { Start = p1, End = p2, Elevation = realWorldZ, IsMajor = isMajor });

                            if (isMajor)
                            {
                                majorPointsBag.Add(p1);
                                majorPointsBag.Add(p2);
                            }
                            else
                            {
                                minorPointsBag.Add(p1);
                                minorPointsBag.Add(p2);
                            }
                        }
                    }
                });
            });

            _contourLines.Clear();
            _contourLines.AddRange(concurrentContours.ToList());

            MinorContoursVisual.Points.Clear();
            MajorContoursVisual.Points.Clear();

            Point3DCollection newMinorPoints = new();
            Point3DCollection newMajorPoints = new();

            foreach (var pt in minorPointsBag) newMinorPoints.Add(pt);
            foreach (var pt in majorPointsBag) newMajorPoints.Add(pt);

            MinorContoursVisual.Points = newMinorPoints;
            MajorContoursVisual.Points = newMajorPoints;

            TxtStatus.Text = $"Status: {_contourLines.Count} Contours Generated";
            BtnGenerateContours.IsEnabled = true;
        }

        private static void CheckTriangleEdgeIntersect(System.Numerics.Vector3 a, System.Numerics.Vector3 b, double targetZ, List<Point3D> points)
        {
            if (a.Z == b.Z) return;
            if (targetZ < Math.Min(a.Z, b.Z) || targetZ > Math.Max(a.Z, b.Z)) return;

            double t = (targetZ - a.Z) / (b.Z - a.Z);
            double x = a.X + t * (b.X - a.X);
            double y = a.Y + t * (b.Y - a.Y);

            points.Add(new Point3D(x, y, targetZ));
        }

        // =========================================================================
        // NATIVE LANDXML CIVIL 3D COMPILER
        // =========================================================================
        private void BtnExportLandXml_Click(object sender, RoutedEventArgs e)
        {
            if (_localTriangles.Count == 0)
            {
                MessageBox.Show("Please load a surface first!", "Export Error");
                return;
            }

            SaveFileDialog dlg = new() { Filter = "LandXML File (*.xml)|*.xml", FileName = "Civil3DSurface.xml" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    ExportToLandXml(dlg.FileName);
                    MessageBox.Show("LandXML exported successfully! You can import this directly into Civil 3D.", "Success");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"LandXML Export Failed: {ex.Message}", "Error");
                }
            }
        }

        private void ExportToLandXml(string filePath)
        {
            using StreamWriter writer = new(filePath);

            writer.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            writer.WriteLine("<LandXML xmlns=\"http://www.landxml.org/schema/LandXML-1.2\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:schemaLocation=\"http://www.landxml.org/schema/LandXML-1.2 http://www.landxml.org/schema/LandXML-1.2/LandXML-1.2.xsd\" version=\"1.2\" date=\"" + DateTime.Now.ToString("yyyy-MM-dd") + "\" time=\"" + DateTime.Now.ToString("HH:mm:ss") + "\">");
            writer.WriteLine("  <Project name=\"CivilStream OpenBIM Export\" />");
            writer.WriteLine("  <Units>");
            writer.WriteLine("    <Metric areaUnit=\"squareMeter\" linearUnit=\"meter\" volumeUnit=\"cubicMeter\" temperatureUnit=\"celsius\" pressureUnit=\"mmHG\" />");
            writer.WriteLine("  </Units>");

            writer.WriteLine("  <Surfaces>");
            writer.WriteLine("    <Surface name=\"Terrain Surface\" desc=\"Triangulated Surface Mesh\">");
            writer.WriteLine("      <Definition surfType=\"TIN\">");
            writer.WriteLine("        <Pnts>");

            Dictionary<System.Numerics.Vector3, int> vertexDict = new();
            List<System.Numerics.Vector3> vertexList = new();
            int pId = 1;

            foreach (var tri in _localTriangles)
            {
                foreach (var v in tri)
                {
                    if (!vertexDict.ContainsKey(v))
                    {
                        vertexList.Add(v);
                        vertexDict[v] = pId;
                        writer.WriteLine($"          <P id=\"{pId}\">{(v.Y + _globalCenterY).ToString("F3", CultureInfo.InvariantCulture)} {(v.X + _globalCenterX).ToString("F3", CultureInfo.InvariantCulture)} {(v.Z + _globalBaseZ).ToString("F3", CultureInfo.InvariantCulture)}</P>");
                        pId++;
                    }
                }
            }
            writer.WriteLine("        </Pnts>");

            writer.WriteLine("        <Faces>");
            foreach (var tri in _localTriangles)
            {
                writer.WriteLine($"          <F>{vertexDict[tri[0]]} {vertexDict[tri[1]]} {vertexDict[tri[2]]}</F>");
            }
            writer.WriteLine("        </Faces>");

            writer.WriteLine("      </Definition>");
            writer.WriteLine("    </Surface>");
            writer.WriteLine("  </Surfaces>");

            if (_contourLines.Count > 0)
            {
                writer.WriteLine("  <PlanFeatures>");

                void WriteFeatureGroup(bool isMajor, string featureName)
                {
                    var segments = _contourLines.Where(c => c.IsMajor == isMajor).ToList();
                    if (segments.Count == 0) return;

                    writer.WriteLine($"    <PlanFeature name=\"{featureName}\">");
                    writer.WriteLine("      <CoordGeom>");
                    foreach (var seg in segments)
                    {
                        writer.WriteLine("        <Line>");
                        writer.WriteLine($"          <Start>{(seg.Start.Y + _globalCenterY).ToString("F3", CultureInfo.InvariantCulture)} {(seg.Start.X + _globalCenterX).ToString("F3", CultureInfo.InvariantCulture)} {seg.Elevation.ToString("F3", CultureInfo.InvariantCulture)}</Start>");
                        writer.WriteLine($"          <End>{(seg.End.Y + _globalCenterY).ToString("F3", CultureInfo.InvariantCulture)} {(seg.End.X + _globalCenterX).ToString("F3", CultureInfo.InvariantCulture)} {seg.Elevation.ToString("F3", CultureInfo.InvariantCulture)}</End>");
                        writer.WriteLine("        </Line>");
                    }
                    writer.WriteLine("      </CoordGeom>");
                    writer.WriteLine("    </PlanFeature>");
                }

                WriteFeatureGroup(true, "Major Contours");
                WriteFeatureGroup(false, "Minor Contours");

                writer.WriteLine("  </PlanFeatures>");
            }

            writer.WriteLine("</LandXML>");
        }

        // =========================================================================
        // NATIVE IFC4 OPEN-BIM COMPILER
        // =========================================================================
        private void BtnExportIfc_Click(object sender, RoutedEventArgs e)
        {
            if (_localTriangles.Count == 0)
            {
                MessageBox.Show("Please load a surface first!", "Export Error");
                return;
            }

            SaveFileDialog dlg = new() { Filter = "IFC BIM File (*.ifc)|*.ifc", FileName = "SiteModel.ifc" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    ExportToIfc4(dlg.FileName);
                    MessageBox.Show("OpenBIM IFC4 exported successfully! You can link this into Revit, Civil 3D, or open in BIMvision.", "Success");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"IFC Export Failed: {ex.Message}", "Error");
                }
            }
        }

        private void ExportToIfc4(string filePath)
        {
            using StreamWriter writer = new(filePath);
            int id = 1;

            static string F(double val) => val.ToString("0.000", CultureInfo.InvariantCulture);
            static string NewGuid() => Convert.ToBase64String(Guid.NewGuid().ToByteArray())
                                       .Replace("+", "_").Replace("/", "$")[..22];

            writer.WriteLine("ISO-10303-21;");
            writer.WriteLine("HEADER;");
            writer.WriteLine("FILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');");
            writer.WriteLine($"FILE_NAME('{System.IO.Path.GetFileName(filePath)}','{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}',('CivilStream'),('CivilStream'),'CivilStream Engine','CivilStream Engine','');");
            writer.WriteLine("FILE_SCHEMA(('IFC4'));");
            writer.WriteLine("ENDSEC;");
            writer.WriteLine("DATA;");

            int orgId = id++; writer.WriteLine($"#{orgId}= IFCORGANIZATION($,'CivilStream',$,$,$);");
            int appId = id++; writer.WriteLine($"#{appId}= IFCAPPLICATION(#{orgId},'1.0','CivilStream','CivilStream');");
            int ownerId = id++; writer.WriteLine($"#{ownerId}= IFCOWNERHISTORY($,#{appId},$,.ADDED.,$,$,$,123456789);");
            int unitId = id++; writer.WriteLine($"#{unitId}= IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);");
            int unitAssId = id++; writer.WriteLine($"#{unitAssId}= IFCUNITASSIGNMENT((#{unitId}));");

            int pt0 = id++; writer.WriteLine($"#{pt0}= IFCCARTESIANPOINT((0.0,0.0,0.0));");
            int dirZ = id++; writer.WriteLine($"#{dirZ}= IFCDIRECTION((0.0,0.0,1.0));");
            int dirX = id++; writer.WriteLine($"#{dirX}= IFCDIRECTION((1.0,0.0,0.0));");
            int ax3d = id++; writer.WriteLine($"#{ax3d}= IFCAXIS2PLACEMENT3D(#{pt0},#{dirZ},#{dirX});");
            int contextId = id++; writer.WriteLine($"#{contextId}= IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.0E-5,#{ax3d},$);");

            int projectId = id++; writer.WriteLine($"#{projectId}= IFCPROJECT('{NewGuid()}',#{ownerId},'CivilStream Project',$,$,$,$,(#{contextId}),#{unitAssId});");
            int sitePlacementId = id++; writer.WriteLine($"#{sitePlacementId}= IFCLOCALPLACEMENT($,#{ax3d});");
            int siteId = id++; writer.WriteLine($"#{siteId}= IFCSITE('{NewGuid()}',#{ownerId},'Site',$,$,#{sitePlacementId},$,$,.ELEMENT.,$,$,$,$,$);");
            int relAggId = id++; writer.WriteLine($"#{relAggId}= IFCRELAGGREGATES('{NewGuid()}',#{ownerId},$,$,#{projectId},(#{siteId}));");

            Dictionary<System.Numerics.Vector3, int> vertexDict = new();
            List<System.Numerics.Vector3> vertexList = new();

            foreach (var tri in _localTriangles)
            {
                foreach (var v in tri)
                {
                    if (!vertexDict.ContainsKey(v))
                    {
                        vertexList.Add(v);
                        vertexDict[v] = vertexList.Count;
                    }
                }
            }

            int ptListId = id++;
            writer.Write($"#{ptListId}= IFCCARTESIANPOINTLIST3D((");
            for (int i = 0; i < vertexList.Count; i++)
            {
                var v = vertexList[i];
                writer.Write($"({F(v.X + _globalCenterX)},{F(v.Y + _globalCenterY)},{F(v.Z + _globalBaseZ)})");
                if (i < vertexList.Count - 1) writer.Write(",");
            }
            writer.WriteLine("));");

            int faceSetId = id++;
            writer.Write($"#{faceSetId}= IFCTRIANGULATEDFACESET(#{ptListId},$,.F.,(");
            for (int i = 0; i < _localTriangles.Count; i++)
            {
                var tri = _localTriangles[i];
                writer.Write($"({vertexDict[tri[0]]},{vertexDict[tri[1]]},{vertexDict[tri[2]]})");
                if (i < _localTriangles.Count - 1) writer.Write(",");
            }
            writer.WriteLine("),$);");

            int shapeRepId = id++; writer.WriteLine($"#{shapeRepId}= IFCSHAPEREPRESENTATION(#{contextId},'Body','Tessellation',(#{faceSetId}));");
            int prodDefId = id++; writer.WriteLine($"#{prodDefId}= IFCPRODUCTDEFINITIONSHAPE($,$,(#{shapeRepId}));");
            int elemPlacementId = id++; writer.WriteLine($"#{elemPlacementId}= IFCLOCALPLACEMENT(#{sitePlacementId},#{ax3d});");
            int terrainId = id++; writer.WriteLine($"#{terrainId}= IFCGEOGRAPHICELEMENT('{NewGuid()}',#{ownerId},'Terrain Surface',$,$,#{elemPlacementId},#{prodDefId},$,$);");

            List<int> allGeoElements = new() { terrainId };

            void WriteIfcContourGroup(bool isMajor, string name)
            {
                var segments = _contourLines.Where(c => c.IsMajor == isMajor).ToList();
                if (segments.Count == 0) return;

                List<int> curveIds = new();
                foreach (var seg in segments)
                {
                    int p1Id = id++; writer.WriteLine($"#{p1Id}= IFCCARTESIANPOINT(({F(seg.Start.X + _globalCenterX)},{F(seg.Start.Y + _globalCenterY)},{F(seg.Elevation)}));");
                    int p2Id = id++; writer.WriteLine($"#{p2Id}= IFCCARTESIANPOINT(({F(seg.End.X + _globalCenterX)},{F(seg.End.Y + _globalCenterY)},{F(seg.Elevation)}));");
                    int polyId = id++; writer.WriteLine($"#{polyId}= IFCPOLYLINE((#{p1Id},#{p2Id}));");
                    curveIds.Add(polyId);
                }

                int curveSetId = id++;
                writer.Write($"#{curveSetId}= IFCGEOMETRICCURVESET((");
                writer.Write(string.Join(",", curveIds.Select(c => $"#{c}")));
                writer.WriteLine("));");

                int cShapeRepId = id++; writer.WriteLine($"#{cShapeRepId}= IFCSHAPEREPRESENTATION(#{contextId},'Body','Curve3D',(#{curveSetId}));");
                int cProdDefId = id++; writer.WriteLine($"#{cProdDefId}= IFCPRODUCTDEFINITIONSHAPE($,$,(#{cShapeRepId}));");
                int cPlacementId = id++; writer.WriteLine($"#{cPlacementId}= IFCLOCALPLACEMENT(#{sitePlacementId},#{ax3d});");
                int contourElementId = id++; writer.WriteLine($"#{contourElementId}= IFCGEOGRAPHICELEMENT('{NewGuid()}',#{ownerId},'{name}',$,$,#{cPlacementId},#{cProdDefId},$,$);");

                allGeoElements.Add(contourElementId);
            }

            WriteIfcContourGroup(true, "Major Contours");
            WriteIfcContourGroup(false, "Minor Contours");

            int relSpatialId = id++;
            writer.WriteLine($"#{relSpatialId}= IFCRELCONTAINEDINSPATIALSTRUCTURE('{NewGuid()}',#{ownerId},'Site Elements',$,({string.Join(",", allGeoElements.Select(e => $"#{e}"))}),#{siteId});");

            writer.WriteLine("ENDSEC;");
            writer.WriteLine("END-ISO-10303-21;");
        }

        // =========================================================================
        // NATIVE DXF EXPORTER (Upgraded to use netDxf)
        // =========================================================================
        private void BtnExportDxf_Click(object sender, RoutedEventArgs e)
        {
            if (_contourLines.Count == 0)
            {
                MessageBox.Show("Please generate contours first!", "Export Error");
                return;
            }

            SaveFileDialog dlg = new() { Filter = "DXF CAD File (*.dxf)|*.dxf", FileName = "SiteContours.dxf" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    DxfDocument doc = new DxfDocument();

                    netDxf.Tables.Layer majorLayer = new netDxf.Tables.Layer("C-TOPO-MAJR") { Color = netDxf.AciColor.Yellow };
                    netDxf.Tables.Layer minorLayer = new netDxf.Tables.Layer("C-TOPO-MINR") { Color = netDxf.AciColor.DarkGray };

                    foreach (var seg in _contourLines)
                    {
                        netDxf.Entities.Line cadLine = new netDxf.Entities.Line(
                            new netDxf.Vector3(seg.Start.X + _globalCenterX, seg.Start.Y + _globalCenterY, seg.Elevation),
                            new netDxf.Vector3(seg.End.X + _globalCenterX, seg.End.Y + _globalCenterY, seg.Elevation)
                        );

                        cadLine.Layer = seg.IsMajor ? majorLayer : minorLayer;
                        doc.Entities.Add(cadLine);
                    }

                    doc.Save(dlg.FileName);
                    MessageBox.Show("DXF Exported successfully using the netDxf Engine!", "Success");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"DXF Export Failed: {ex.Message}", "Error");
                }
            }
        }

        // Distance-based Laplacian smoothing filter for grid elevations
        private static List<CivilPoint> SmoothElevations(List<CivilPoint> points, double spacing, double alpha = 0.6, int iterations = 3)
        {
            var currentPoints = points.Select(p => new CivilPoint(p.X, p.Y, p.Z)).ToList();
            double neighborThreshold = 1.5 * spacing;

            for (int iter = 0; iter < iterations; iter++)
            {
                var nextPoints = currentPoints.Select(p => new CivilPoint(p.X, p.Y, p.Z)).ToList();

                for (int i = 0; i < currentPoints.Count; i++)
                {
                    var p = currentPoints[i];
                    double sumZ = 0;
                    int count = 0;

                    for (int j = 0; j < currentPoints.Count; j++)
                    {
                        if (i == j) continue;
                        var neighbor = currentPoints[j];

                        double dx = p.X - neighbor.X;
                        double dy = p.Y - neighbor.Y;
                        double dist = Math.Sqrt(dx * dx + dy * dy);

                        if (dist <= neighborThreshold)
                        {
                            sumZ += neighbor.Z;
                            count++;
                        }
                    }

                    if (count > 0)
                    {
                        double avgZ = sumZ / count;
                        nextPoints[i].Z = (1.0 - alpha) * p.Z + alpha * avgZ;
                    }
                }

                currentPoints = nextPoints;
            }

            return currentPoints;
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. Clear 3D Viewport Content
                TerrainModelContainer.Content = null;
                _terrainModel = null;
                _localTriangles.Clear();

                // 2. Clear Visuals (Contours, Wireframes, Profiles)
                _wireframePoints.Clear();
                _wireframeVisual.Points = new Point3DCollection();
                if (Viewport.Children.Contains(_wireframeVisual))
                    Viewport.Children.Remove(_wireframeVisual);

                _contourLines.Clear();
                MinorContoursVisual.Points = new Point3DCollection();
                MajorContoursVisual.Points = new Point3DCollection();

                ProfileLineVisual.Points = new Point3DCollection();
                ProfileTracker3D.Radius = 0;

                // 3. Clear Profile Section (keeping only the text placeholder)
                _rawProfileData.Clear();
                _rawProfile3DData.Clear();

                var toRemove = new List<System.Windows.UIElement>();
                foreach (System.Windows.UIElement child in ProfileCanvas.Children)
                {
                    if (child != TxtCanvasPlaceholder)
                        toRemove.Add(child);
                }
                foreach (var el in toRemove)
                {
                    ProfileCanvas.Children.Remove(el);
                }

                if (TxtCanvasPlaceholder != null)
                    TxtCanvasPlaceholder.Visibility = Visibility.Visible;

                if (ProfileSlider != null)
                    ProfileSlider.Visibility = Visibility.Collapsed;

                // 4. Reset Geodetic Projection variables
                _globalCenterX = 0;
                _globalCenterY = 0;
                _globalBaseZ = 0;

                // 5. Reset Satellite states
                _isSatelliteAvailable = false;
                _isSatelliteActive = false;
                BtnToggleSatellite.Background = new SolidColorBrush(Color.FromRgb(26, 26, 26));

                // 6. Reset UI highlights and labels
                _isWireframeActive = false;
                BtnToggleWireframe.Background = new SolidColorBrush(Color.FromRgb(26, 26, 26));

                _isHeatmapActive = true;
                BtnToggleHeatmap.Background = new SolidColorBrush(Color.FromRgb(56, 189, 248));

                _isProfileModeActive = false;
                BtnProfileTool.Background = new SolidColorBrush(Color.FromRgb(26, 26, 26));
                _profilePoints.Clear();
                _vertexDistances.Clear();
                if (ProfileEditorPanel != null)
                {
                    ProfileEditorPanel.Visibility = Visibility.Collapsed;
                    StackProfileVertices.Children.Clear();
                }

                ElevationLegend.Visibility = Visibility.Collapsed;

                TxtPointCount.Text = "Points Loaded: 0";
                TxtStatus.Text = "Status: Workspace Reset (Ready)";
                TxtStaticZ.Text = "---";
                TxtStaticE.Text = "---";
                TxtStaticN.Text = "---";

                MessageBox.Show("Workspace cleared successfully! You can now load a new survey or KML boundary file.", "Workspace Reset", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Reset failed: {ex.Message}", "Error");
            }
        }

        private string GenerateMockAsciiGrid(double west, double east, double south, double north)
        {
            int ncols = 100;
            int nrows = 100;
            double cellsizeX = (east - west) / ncols;
            double cellsizeY = (north - south) / nrows;
            double cellsize = Math.Max(cellsizeX, cellsizeY);

            var sb = new StringBuilder();
            sb.AppendLine($"ncols {ncols}");
            sb.AppendLine($"nrows {nrows}");
            sb.AppendLine($"xllcorner {west.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"yllcorner {south.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"cellsize {cellsize.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine("nodata_value -9999");

            for (int r = 0; r < nrows; r++)
            {
                var rowValues = new List<string>();
                for (int c = 0; c < ncols; c++)
                {
                    double nx = (double)c / ncols;
                    double ny = (double)r / nrows;
                    
                    // Generate a nice rolling terrain with low frequency waves and micro-texture
                    double baseElev = 100.0 + nx * 50.0 + ny * 30.0;
                    double wave = 25.0 * Math.Sin(nx * Math.PI * 3.0) * Math.Cos(ny * Math.PI * 2.5);
                    double noise = 4.0 * Math.Sin(nx * Math.PI * 15.0) * Math.Sin(ny * Math.PI * 15.0);
                    double elev = baseElev + wave + noise;
                    
                    rowValues.Add(elev.ToString("F2", CultureInfo.InvariantCulture));
                }
                sb.AppendLine(string.Join(" ", rowValues));
            }
            return sb.ToString();
        }

        private void CreateMockSatelliteImage(string outputPath)
        {
            try
            {
                int width = 1024;
                int height = 1024;
                var drawingVisual = new DrawingVisual();
                using (var drawingContext = drawingVisual.RenderOpen())
                {
                    // 1. Draw rich green background (fields)
                    drawingContext.DrawRectangle(
                        new SolidColorBrush(Color.FromRgb(34, 139, 34)), // ForestGreen
                        null,
                        new Rect(0, 0, width, height));

                    // 2. Draw some crop fields (brown/yellow rectangles)
                    var pen = new Pen(Brushes.DarkGoldenrod, 2);
                    drawingContext.DrawRectangle(Brushes.Olive, pen, new Rect(100, 100, 300, 250));
                    drawingContext.DrawRectangle(Brushes.DarkKhaki, pen, new Rect(600, 150, 250, 300));
                    drawingContext.DrawRectangle(Brushes.SaddleBrown, pen, new Rect(150, 600, 400, 300));

                    // 3. Draw a blue winding river (curved path)
                    var riverPen = new Pen(Brushes.DodgerBlue, 40)
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round,
                        LineJoin = PenLineJoin.Round
                    };
                    var geometry = new StreamGeometry();
                    using (var ctx = geometry.Open())
                    {
                        ctx.BeginFigure(new System.Windows.Point(0, 50), false, false);
                        ctx.BezierTo(
                            new System.Windows.Point(300, 100),
                            new System.Windows.Point(400, 800),
                            new System.Windows.Point(1024, 900),
                            true,
                            true);
                    }
                    drawingContext.DrawGeometry(null, riverPen, geometry);

                    // 4. Draw a grey highway crossing the river
                    var roadPen = new Pen(Brushes.DimGray, 15);
                    drawingContext.DrawLine(roadPen, new System.Windows.Point(0, 800), new System.Windows.Point(1024, 200));
                }

                var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(drawingVisual);

                var encoder = new JpegBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = new FileStream(outputPath, FileMode.Create))
                {
                    encoder.Save(fs);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to generate mock satellite image: {ex.Message}");
            }
        }

        private void PopulateProfileVerticesEditor()
        {
            try
            {
                StackProfileVertices.Children.Clear();
                if (_profilePoints.Count < 2)
                {
                    ProfileEditorPanel.Visibility = Visibility.Collapsed;
                    return;
                }

                ProfileEditorPanel.Visibility = Visibility.Visible;

                Dispatcher.BeginInvoke(new Action(() => {
                    if (SidebarScrollViewer != null)
                        SidebarScrollViewer.ScrollToBottom();
                }), System.Windows.Threading.DispatcherPriority.Background);

            for (int i = 0; i < _profilePoints.Count; i++)
            {
                int index = i;
                var pt = _profilePoints[i];
                double realX = pt.X + _globalCenterX;
                double realY = pt.Y + _globalCenterY;

                string labelText = $"V{index + 1}";
                if (index == 0) labelText += " (Start)";
                else if (index == _profilePoints.Count - 1) labelText += " (End)";
                else labelText += " (PI)";

                Grid rowGrid = new() { Margin = new Thickness(0, 0, 0, 8) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                TextBlock lbl = new()
                {
                    Text = labelText,
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(lbl, 0);
                rowGrid.Children.Add(lbl);

                UIElement CreateCoordEditor(string name, double currentVal, Action<double> onUpdate)
                {
                    Grid editorGrid = new() { Margin = new Thickness(2, 0, 2, 0) };
                    editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                    editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });

                    TextBox txtVal = new()
                    {
                        Text = currentVal.ToString("F1", CultureInfo.InvariantCulture),
                        Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                        Foreground = Brushes.White,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(61, 61, 61)),
                        Padding = new Thickness(3, 2, 3, 2),
                        FontSize = 10,
                        FontFamily = new FontFamily("Consolas")
                    };
                    Grid.SetColumn(txtVal, 0);
                    editorGrid.Children.Add(txtVal);

                    void TriggerUpdate()
                    {
                        if (double.TryParse(txtVal.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedVal))
                        {
                            onUpdate(parsedVal);
                        }
                    }

                    txtVal.LostFocus += (s, e) => TriggerUpdate();
                    txtVal.KeyDown += (s, e) => { if (e.Key == Key.Enter) { TriggerUpdate(); Keyboard.ClearFocus(); } };

                    Button btnDec = new()
                    {
                        Content = "-",
                        Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                        Foreground = Brushes.White,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(61, 61, 61)),
                        FontSize = 8,
                        FontWeight = FontWeights.Bold,
                        Cursor = Cursors.Hand
                    };
                    Grid.SetColumn(btnDec, 1);
                    editorGrid.Children.Add(btnDec);
                    btnDec.Click += (s, e) =>
                    {
                        if (double.TryParse(txtVal.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedVal))
                        {
                            double newVal = parsedVal - 1.0;
                            txtVal.Text = newVal.ToString("F1", CultureInfo.InvariantCulture);
                            onUpdate(newVal);
                        }
                    };

                    Button btnInc = new()
                    {
                        Content = "+",
                        Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                        Foreground = Brushes.White,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(61, 61, 61)),
                        FontSize = 8,
                        FontWeight = FontWeights.Bold,
                        Cursor = Cursors.Hand
                    };
                    Grid.SetColumn(btnInc, 2);
                    editorGrid.Children.Add(btnInc);
                    btnInc.Click += (s, e) =>
                    {
                        if (double.TryParse(txtVal.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedVal))
                        {
                            double newVal = parsedVal + 1.0;
                            txtVal.Text = newVal.ToString("F1", CultureInfo.InvariantCulture);
                            onUpdate(newVal);
                        }
                    };

                    return editorGrid;
                }

                var xEditor = CreateCoordEditor("X", realX, (newX) =>
                {
                    double localX = newX - _globalCenterX;
                    double? pz = GetElevationAt(localX, pt.Y);
                    double z = pz ?? pt.Z;
                    _profilePoints[index] = new Point3D(localX, pt.Y, z);
                    UpdateAlignmentAfterTweak();
                });
                Grid.SetColumn(xEditor, 1);
                rowGrid.Children.Add(xEditor);

                var yEditor = CreateCoordEditor("Y", realY, (newY) =>
                {
                    double localY = newY - _globalCenterY;
                    double? pz = GetElevationAt(pt.X, localY);
                    double z = pz ?? pt.Z;
                    _profilePoints[index] = new Point3D(pt.X, localY, z);
                    UpdateAlignmentAfterTweak();
                });
                Grid.SetColumn(yEditor, 2);
                rowGrid.Children.Add(yEditor);

                StackProfileVertices.Children.Add(rowGrid);
            }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error in PopulateProfileVerticesEditor: {ex.Message}\n{ex.StackTrace}", "Debug Error");
            }
        }

        private void UpdateAlignmentAfterTweak()
        {
            Update3DProfilePreview();
            Generate2DProfile();
            RedrawProfileCanvas();
        }
    }
}