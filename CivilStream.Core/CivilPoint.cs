using System;

namespace CivilStream.Core
{
    public class CivilPoint
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public CivilPoint(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        // Computes three-dimensional distance between survey marks
        public double DistanceTo(CivilPoint other)
        {
            double dx = other.X - this.X;
            double dy = other.Y - this.Y;
            double dz = other.Z - this.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}