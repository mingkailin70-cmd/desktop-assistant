using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace XiaoK.PetRuntimePrototype;

internal enum ThreeDPetPose
{
    Idle,
    Listening,
    Thinking,
    Executing
}

/// <summary>用WPF原生3D几何生成隔离用的银渐层猫形象，不读取外部模型或联网。</summary>
internal static class ThreeDPetScene
{
    public static Viewport3D Create(ThreeDPetPose pose)
    {
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(116, 118, 122)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(248, 250, 255), new Vector3D(-1.2, -1.5, -3)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(148, 151, 158), new Vector3D(1.5, 0.4, 2)));

        var silver = CreateSilverMaterial();
        var silverDark = CreateSolidMaterial(Color.FromRgb(108, 111, 118), 28);
        var white = CreateSolidMaterial(Color.FromRgb(249, 246, 242), 18);
        var pink = CreateSolidMaterial(Color.FromRgb(219, 112, 139), 4);
        var dark = CreateSolidMaterial(Color.FromRgb(38, 43, 57), 30);
        var amber = CreateSolidMaterial(Color.FromRgb(193, 145, 73), 22);

        AddTube(scene,
            [new Point3D(0.48, -0.36, -0.28), new Point3D(0.91, -0.22, -0.26), new Point3D(1.18, 0.06, -0.22), new Point3D(1.1, 0.48, -0.16), new Point3D(0.86, 0.62, -0.12)],
            [0.16, 0.145, 0.13, 0.115, 0.09], 10, silver);

        AddSphere(scene, new Point3D(0, -0.18, 0), new Vector3D(0.69, 0.9, 0.51), silver);
        AddSphere(scene, new Point3D(0, -0.32, 0.443), new Vector3D(0.36, 0.52, 0.12), white);
        AddSphere(scene, new Point3D(-0.43, -0.73, 0.04), new Vector3D(0.34, 0.35, 0.38), silverDark);
        AddSphere(scene, new Point3D(0.43, -0.73, 0.04), new Vector3D(0.34, 0.35, 0.38), silverDark);

        var leftPaw = pose == ThreeDPetPose.Executing
            ? new Point3D(-0.52, -0.22, 0.51)
            : new Point3D(-0.34, -0.72, 0.38);
        var leftPawScale = pose == ThreeDPetPose.Executing
            ? new Vector3D(0.22, 0.35, 0.22)
            : new Vector3D(0.22, 0.43, 0.23);
        AddSphere(scene, leftPaw, leftPawScale, silver);
        AddSphere(scene, new Point3D(0.34, -0.72, 0.38), new Vector3D(0.22, 0.43, 0.23), silver);
        AddSphere(scene, new Point3D(-0.52, -0.46, 0.601), new Vector3D(0.14, 0.09, 0.045), white);
        AddSphere(scene, new Point3D(0.34, -0.96, 0.535), new Vector3D(0.14, 0.09, 0.045), white);

        var head = new Model3DGroup();
        AddEar(head, -1, silver, pink);
        AddEar(head, 1, silver, pink);
        AddSphere(head, new Point3D(0, 0.9, 0.15), new Vector3D(0.77, 0.72, 0.57), silver);
        AddSphere(head, new Point3D(-0.39, 0.65, 0.59), new Vector3D(0.34, 0.25, 0.18), white);
        AddSphere(head, new Point3D(0.39, 0.65, 0.59), new Vector3D(0.34, 0.25, 0.18), white);
        AddSphere(head, new Point3D(-0.275, 0.98, 0.691), new Vector3D(0.12, 0.165, 0.072), amber);
        AddSphere(head, new Point3D(0.275, 0.98, 0.691), new Vector3D(0.12, 0.165, 0.072), amber);
        AddSphere(head, new Point3D(-0.26, 0.98, 0.75), new Vector3D(0.054, 0.118, 0.035), dark);
        AddSphere(head, new Point3D(0.29, 0.98, 0.75), new Vector3D(0.054, 0.118, 0.035), dark);
        AddSphere(head, new Point3D(-0.285, 1.05, 0.783), new Vector3D(0.027, 0.035, 0.018), white);
        AddSphere(head, new Point3D(0.265, 1.05, 0.783), new Vector3D(0.027, 0.035, 0.018), white);
        AddTriangle(head,
            new Point3D(-0.095, 0.72, 0.782), new Point3D(0.095, 0.72, 0.782), new Point3D(0, 0.62, 0.812), pink);
        AddTube(head,
            [new Point3D(0, 0.63, 0.805), new Point3D(0, 0.55, 0.812), new Point3D(-0.075, 0.51, 0.795)],
            [0.018, 0.016, 0.012], 6, dark);
        AddTube(head,
            [new Point3D(0, 0.55, 0.812), new Point3D(0.075, 0.51, 0.795)],
            [0.016, 0.012], 6, dark);

        AddWhisker(head, -1, 0.65, silverDark);
        AddWhisker(head, 1, 0.65, silverDark);
        var headTilt = pose switch
        {
            ThreeDPetPose.Listening => -7.0,
            ThreeDPetPose.Thinking => 8.0,
            _ => 0.0
        };
        head.Transform = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), headTilt),
            new Point3D(0, 0.86, 0.18));
        scene.Children.Add(head);

        if (pose == ThreeDPetPose.Listening)
        {
            AddTube(scene,
                [new Point3D(0.98, 1.03, 0.25), new Point3D(1.08, 1.16, 0.25), new Point3D(0.98, 1.29, 0.25)],
                [0.022, 0.022, 0.022], 7, CreateSolidMaterial(Color.FromRgb(119, 205, 231), 15));
            AddTube(scene,
                [new Point3D(1.12, 0.99, 0.24), new Point3D(1.25, 1.16, 0.24), new Point3D(1.12, 1.33, 0.24)],
                [0.02, 0.02, 0.02], 7, CreateSolidMaterial(Color.FromRgb(119, 205, 231), 15));
        }
        else if (pose == ThreeDPetPose.Thinking)
        {
            var thought = CreateSolidMaterial(Color.FromRgb(197, 184, 226), 16);
            AddSphere(scene, new Point3D(-0.92, 1.55, 0.04), new Vector3D(0.12, 0.12, 0.1), thought);
            AddSphere(scene, new Point3D(-1.08, 1.75, 0.02), new Vector3D(0.085, 0.085, 0.075), thought);
            AddSphere(scene, new Point3D(-1.2, 1.9, 0), new Vector3D(0.055, 0.055, 0.05), thought);
        }
        else if (pose == ThreeDPetPose.Executing)
        {
            var gold = CreateSolidMaterial(Color.FromRgb(245, 198, 105), 20);
            AddSphere(scene, new Point3D(-0.84, 0.16, 0.53), new Vector3D(0.07, 0.07, 0.06), gold);
            AddSphere(scene, new Point3D(-0.72, 0.35, 0.47), new Vector3D(0.045, 0.045, 0.04), gold);
            AddSphere(scene, new Point3D(-0.96, 0.39, 0.38), new Vector3D(0.04, 0.04, 0.035), gold);
        }

        var viewport = new Viewport3D
        {
            Width = 512,
            Height = 512,
            ClipToBounds = true,
            Camera = new PerspectiveCamera
            {
                Position = new Point3D(0, 0.42, 7.1),
                LookDirection = new Vector3D(0, -0.02, -7.1),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 31
            }
        };
        viewport.Children.Add(new ModelVisual3D { Content = scene });
        return viewport;
    }

    private static void AddWhisker(Model3DGroup group, int side, double y, Material material)
    {
        var startX = 0.55 * side;
        AddTube(group,
            [new Point3D(startX, y + 0.06, 0.69), new Point3D(0.82 * side, y + 0.11, 0.68), new Point3D(1.02 * side, y + 0.16, 0.62)],
            [0.012, 0.01, 0.007], 5, material);
        AddTube(group,
            [new Point3D(startX, y - 0.01, 0.69), new Point3D(0.83 * side, y - 0.03, 0.67), new Point3D(1.03 * side, y - 0.08, 0.61)],
            [0.012, 0.01, 0.007], 5, material);
    }

    private static void AddEar(Model3DGroup group, int side, Material outer, Material inner)
    {
        var outerPoints = side < 0
            ? new[] { new Point3D(-0.79, 1.34, 0.08), new Point3D(-0.15, 1.43, 0.08), new Point3D(-0.64, 2.12, 0.08) }
            : new[] { new Point3D(0.15, 1.43, 0.08), new Point3D(0.79, 1.34, 0.08), new Point3D(0.64, 2.12, 0.08) };
        AddTriangle(group, outerPoints[0], outerPoints[1], outerPoints[2], outer, depth: 0.2);
        var innerPoints = side < 0
            ? new[] { new Point3D(-0.67, 1.48, 0.19), new Point3D(-0.29, 1.51, 0.19), new Point3D(-0.625, 1.99, 0.19) }
            : new[] { new Point3D(0.29, 1.51, 0.19), new Point3D(0.67, 1.48, 0.19), new Point3D(0.625, 1.99, 0.19) };
        AddTriangle(group, innerPoints[0], innerPoints[1], innerPoints[2], inner);
    }

    private static void AddSphere(Model3DGroup group, Point3D center, Vector3D scale, Material material)
    {
        const int longitudeSegments = 32;
        const int latitudeSegments = 20;
        var mesh = new MeshGeometry3D();
        for (var latitude = 0; latitude <= latitudeSegments; latitude++)
        {
            var phi = Math.PI * latitude / latitudeSegments;
            var y = Math.Cos(phi);
            var ringRadius = Math.Sin(phi);
            for (var longitude = 0; longitude <= longitudeSegments; longitude++)
            {
                var theta = Math.PI * 2 * longitude / longitudeSegments;
                var normal = new Vector3D(ringRadius * Math.Cos(theta), y, ringRadius * Math.Sin(theta));
                mesh.Positions.Add(new Point3D(normal.X, normal.Y, normal.Z));
                mesh.Normals.Add(normal);
                mesh.TextureCoordinates.Add(new Point((double)longitude / longitudeSegments, (double)latitude / latitudeSegments));
            }
        }

        for (var latitude = 0; latitude < latitudeSegments; latitude++)
        for (var longitude = 0; longitude < longitudeSegments; longitude++)
        {
            var a = latitude * (longitudeSegments + 1) + longitude;
            var b = a + longitudeSegments + 1;
            mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(a + 1);
            mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 1);
        }

        var transform = new Transform3DGroup();
        transform.Children.Add(new ScaleTransform3D(scale));
        transform.Children.Add(new TranslateTransform3D(center.X, center.Y, center.Z));
        group.Children.Add(new GeometryModel3D(mesh, material)
        {
            BackMaterial = material,
            Transform = transform
        });
    }

    private static void AddTriangle(Model3DGroup group, Point3D a, Point3D b, Point3D c, Material material, double depth = 0)
    {
        var mesh = new MeshGeometry3D();
        var points = depth <= 0 ? new[] { a, b, c } : new[]
        {
            a, b, c,
            new Point3D(a.X, a.Y, a.Z - depth),
            new Point3D(b.X, b.Y, b.Z - depth),
            new Point3D(c.X, c.Y, c.Z - depth)
        };
        foreach (var point in points)
        {
            mesh.Positions.Add(point);
            mesh.Normals.Add(new Vector3D(0, 0, 1));
        }
        mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(1); mesh.TriangleIndices.Add(2);
        if (depth > 0)
        {
            mesh.TriangleIndices.Add(3); mesh.TriangleIndices.Add(5); mesh.TriangleIndices.Add(4);
            mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(3); mesh.TriangleIndices.Add(4);
            mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(4); mesh.TriangleIndices.Add(1);
            mesh.TriangleIndices.Add(1); mesh.TriangleIndices.Add(4); mesh.TriangleIndices.Add(5);
            mesh.TriangleIndices.Add(1); mesh.TriangleIndices.Add(5); mesh.TriangleIndices.Add(2);
            mesh.TriangleIndices.Add(2); mesh.TriangleIndices.Add(5); mesh.TriangleIndices.Add(3);
            mesh.TriangleIndices.Add(2); mesh.TriangleIndices.Add(3); mesh.TriangleIndices.Add(0);
        }
        group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
    }

    private static void AddTube(Model3DGroup group, IReadOnlyList<Point3D> points, IReadOnlyList<double> radii,
        int radialSegments, Material material)
    {
        if (points.Count < 2 || points.Count != radii.Count) throw new ArgumentException("3D线段点与半径数量无效。");
        var smoothPoints = new List<Point3D>();
        var smoothRadii = new List<double>();
        const int subdivisions = 4;
        for (var index = 0; index < points.Count - 1; index++)
        {
            var p0 = points[Math.Max(0, index - 1)];
            var p1 = points[index];
            var p2 = points[index + 1];
            var p3 = points[Math.Min(points.Count - 1, index + 2)];
            for (var step = 0; step < subdivisions; step++)
            {
                var t = (double)step / subdivisions;
                smoothPoints.Add(new Point3D(
                    CatmullRom(p0.X, p1.X, p2.X, p3.X, t),
                    CatmullRom(p0.Y, p1.Y, p2.Y, p3.Y, t),
                    CatmullRom(p0.Z, p1.Z, p2.Z, p3.Z, t)));
                smoothRadii.Add(radii[index] + (radii[index + 1] - radii[index]) * t);
            }
        }
        smoothPoints.Add(points[^1]);
        smoothRadii.Add(radii[^1]);

        var mesh = new MeshGeometry3D();
        for (var index = 0; index < smoothPoints.Count; index++)
        {
            var tangent = smoothPoints[Math.Min(index + 1, smoothPoints.Count - 1)] - smoothPoints[Math.Max(index - 1, 0)];
            tangent.Normalize();
            var axisA = Vector3D.CrossProduct(tangent, new Vector3D(0, 0, 1));
            if (axisA.Length < 0.0001) axisA = Vector3D.CrossProduct(tangent, new Vector3D(0, 1, 0));
            axisA.Normalize();
            var axisB = Vector3D.CrossProduct(tangent, axisA);
            axisB.Normalize();
            for (var segment = 0; segment <= radialSegments; segment++)
            {
                var angle = Math.PI * 2 * segment / radialSegments;
                var normal = axisA * Math.Cos(angle) + axisB * Math.Sin(angle);
                mesh.Positions.Add(smoothPoints[index] + normal * smoothRadii[index]);
                mesh.Normals.Add(normal);
                mesh.TextureCoordinates.Add(new Point((double)segment / radialSegments, (double)index / (smoothPoints.Count - 1)));
            }
        }

        for (var index = 0; index < smoothPoints.Count - 1; index++)
        for (var segment = 0; segment < radialSegments; segment++)
        {
            var a = index * (radialSegments + 1) + segment;
            var b = a + radialSegments + 1;
            mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b);
            mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b + 1); mesh.TriangleIndices.Add(b);
        }
        group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
    }

    private static double CatmullRom(double p0, double p1, double p2, double p3, double t) =>
        0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t
            + (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t);

    private static Material CreateSilverMaterial()
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(241, 242, 245), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(181, 185, 192), 0.48));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(105, 108, 116), 1));
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(gradient));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(248, 250, 255)), 38));
        return material;
    }

    private static Material CreateSolidMaterial(Color color, double gloss)
    {
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
        var highlight = Color.FromRgb(
            (byte)Math.Min(255, color.R + 28),
            (byte)Math.Min(255, color.G + 28),
            (byte)Math.Min(255, color.B + 28));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(highlight), gloss));
        return material;
    }
}
