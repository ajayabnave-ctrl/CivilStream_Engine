using System;
using NetTopologySuite.Geometries;

namespace CivilStream.Core
{
    /// <summary>
    /// Simple equirectangular projection for small geographic extents.
    /// Converts latitude/longitude (WGS84) to a local Cartesian coordinate system (meters).
    /// The origin is set to the centre of the area of interest to keep values small.
    /// </summary>
    public static class CoordinateProjection
    {
        private const double EarthRadius = 6378137.0; // meters (WGS84 mean radius)
        private static double _originLatRad;
        private static double _originLonRad;
        private static double _cosLat;
        private static bool _isInitialized = false;

        /// <summary>
        /// Initialise the projection with a central latitude/longitude (degrees).
        /// Must be called before any conversion.
        /// </summary>
        public static void Initialise(double latitudeDeg, double longitudeDeg)
        {
            _originLatRad = latitudeDeg * Math.PI / 180.0;
            _originLonRad = longitudeDeg * Math.PI / 180.0;
            _cosLat = Math.Cos(_originLatRad);
            _isInitialized = true;
        }

        /// <summary>
        /// Convert geographic coordinates (degrees) to local Cartesian (meters).
        /// </summary>
        public static (double X, double Y) ToLocal(double latitudeDeg, double longitudeDeg)
        {
            if (!_isInitialized) throw new InvalidOperationException("CoordinateProjection not initialised.");
            double latRad = latitudeDeg * Math.PI / 180.0;
            double lonRad = longitudeDeg * Math.PI / 180.0;
            double dLat = latRad - _originLatRad;
            double dLon = (lonRad - _originLonRad) * _cosLat; // scale longitude by cos(latitude)
            double x = dLon * EarthRadius;
            double y = dLat * EarthRadius;
            return (x, y);
        }

        /// <summary>
        /// Convert local Cartesian coordinates (meters) back to geographic degrees.
        /// </summary>
        public static (double LatitudeDeg, double LongitudeDeg) ToGeodetic(double x, double y)
        {
            if (!_isInitialized) throw new InvalidOperationException("CoordinateProjection not initialised.");
            double dLat = y / EarthRadius;
            double dLon = x / (EarthRadius * _cosLat);
            double latRad = _originLatRad + dLat;
            double lonRad = _originLonRad + dLon;
            return (latRad * 180.0 / Math.PI, lonRad * 180.0 / Math.PI);
        }
    }
}
