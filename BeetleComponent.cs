using Eto.Forms;
using Grasshopper;
using Grasshopper.Kernel;
using Rhino;
using Rhino.Geometry;
using System;
using System.Collections.Generic;

namespace Beetle
{
    public class BeetleComponent : GH_Component
    {
        private static readonly Dictionary<string, double> WallUValues = new Dictionary<string, double>
        {
            {"Uninsulated Brick (200mm)", 2.0 },
            {"Basic Insulation (100mm)", 0.35 },
            {"Double Insulation (200mm)", 0.20 },
            {"Passivhaus Standard (300mm)", 0.15 },
            {"Custom", 0.3 },
        };

        private static readonly Dictionary<string, double> WindowUValues = new Dictionary<string, double>
        {
            {"Single Glazing", 5.0 },
            {"Double Glazing", 1.4 },
            {"Triple Glazing", 0.8 },
            {"Custom", 1.4 }
        };

        private static readonly Dictionary<string, (double winter, double summer)> ClimateData = new Dictionary<string, (double, double)>
        {
            {"London, UK", (5.0, 22.0) },
            { "Oslo, Norway", (-5.0, 18.0) },
            { "Barcelona, Spain", (10.0, 28.0) },
            { "New York, USA", (-2.0, 25.0) },
            { "Sydney, Australia", (12.0, 26.0) },
            { "Custom", (5.0, 22.0) }
        };

        public BeetleComponent()
          : base("Thermal Envelop Optimiser", "ThermalOpt",
            "Analyses and optimises room geometry for thermal performance",
            "MyTools", "Thermal")
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            // Geometry inputs
            pManager.AddBoxParameter("Room", "R", "Room as a box", GH_ParamAccess.item);
            pManager.AddNumberParameter("WindowPercentage", "W%", "Window area as percentage of wall area (0-100", GH_ParamAccess.item, 20.0);

            //Environmental inputs
            pManager.AddNumberParameter("ExternalTemp", "Te", "External temperature (°C)", GH_ParamAccess.item, 5.0);
            pManager.AddNumberParameter("DesiredTemp", "Ti", " Desired internal temperature (°C)", GH_ParamAccess.item, 20.0);

            //Material inputs
            pManager.AddNumberParameter("WallUValue", "Uw", "Wall U-value (W/m²K) - lower is better", GH_ParamAccess.item, 0.3) ;
            pManager.AddNumberParameter("WindowUValue", "Uwi", "Window U-value (W/m²K) - lower is better", GH_ParamAccess.item, 1.4);
            pManager.AddNumberParameter("RoofUValue", "Ur", "Roof U-value (W/m²K) - lower is better", GH_ParamAccess.item, 0.2);
            pManager.AddNumberParameter("FloorUValue", "Uf", "Floor U-value (W/m²K) - lower is better", GH_ParamAccess.item, 0.25);

            //Heat and solar inputs
            pManager.AddNumberParameter("InternalGains", "Ig", "Internal heat gains from people/equipment (Watts)", GH_ParamAccess.item, 200.0);
            pManager.AddNumberParameter("SolarGains", "Sg", "Solar heat gains (Watts) - optional", GH_ParamAccess.item, 0.0);

            //Optimisation target
            pManager.AddNumberParameter("TargetHeatLoss", "Th", "Target heat loss (Watts) - 0 for analysis only", GH_ParamAccess.item, 0.0);
            pManager.AddBooleanParameter("Optimise", "O", "Generate optimised geometry", GH_ParamAccess.item, false);

            pManager[9].Optional = true;
            pManager[10].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("TotalHeatLoss", "Q,", "Total heat loss through envelope (Watts)", GH_ParamAccess.item);
            pManager.AddNumberParameter("NetHeating", "Qnet", "Net heating requirement (Watts)", GH_ParamAccess.item);
            pManager.AddNumberParameter("WallLoss", "Qw", "Heat loss through walls (Watts)", GH_ParamAccess.item);
            pManager.AddNumberParameter("WindowLoss", "Qwi", "Heat loss through windows (Watts)", GH_ParamAccess.item);
            pManager.AddNumberParameter("RoofLoss", "Qr", "Heat loss through roof (Watts)", GH_ParamAccess.item);
            pManager.AddNumberParameter("FloorLoss", "Qf", "Heat loss through floor (Watts)", GH_ParamAccess.item);

            pManager.AddNumberParameter("SurfaceToVolume", "S/V", "Surface to volume ratio", GH_ParamAccess.item);
            pManager.AddNumberParameter("HeatLossPerM2", "Q/m²", "Heat loss per square meter floor area (W/m²)", GH_ParamAccess.item);

            pManager.AddNumberParameter("RecommendedWallU", "Uw*", "Recommended wall U-value for target", GH_ParamAccess.item);
            pManager.AddNumberParameter("RecommendedInsulation", "t*", "Additional insulation thickness needed (mm)", GH_ParamAccess.item);
            pManager.AddBoxParameter("OptimizedRoom", "R*", "Room with optimized wall thickness", GH_ParamAccess.item);

            pManager.AddTextParameter("Report", "Info", "Performance report", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Box room = Box.Empty;
            double windowPercentage = 20.0;
            double externalTemp = 5.0;
            double desiredTemp = 20.0;
            double wallUValue = 0.3;
            double windowUValue = 1.4;
            double roofUValue = 0.2;
            double floorUValue = 0.25;
            double internalGains = 200.0;
            double solarGains = 0.0;
            double targetHeatLoss = 0.0;
            bool optimise = false;

            if (!DA.GetData<Box>(0, ref room)) return;
            if (!DA.GetData(1, ref windowPercentage)) return;
            if (!DA.GetData(2, ref externalTemp)) return;
            if (!DA.GetData(3, ref desiredTemp)) return;
            if (!DA.GetData(4, ref wallUValue)) return;
            if (!DA.GetData(5, ref windowUValue)) return;
            if (!DA.GetData(6, ref roofUValue)) return;
            if (!DA.GetData(7, ref floorUValue)) return;
            if (!DA.GetData(8, ref internalGains)) return;
            DA.GetData(9, ref solarGains);
            DA.GetData(10, ref targetHeatLoss);
            if (!DA.GetData(11, ref optimise)) return;

            if (windowPercentage < 0 || windowPercentage > 100)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Window percentage must be between 0 and 100");
                return;
            }

            if (!room.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid room geometry");
                return;
            }

            try
            {
                double length = room.X.Length;
                double width = room.Y.Length;
                double height = room.Z.Length;

                double floorArea = length * width;
                double wallArea = 2 * (length + width) * height;
                double roofArea = length * width;

                double windowArea = wallArea * (windowPercentage / 100.0);
                double opaqueWallArea = wallArea - windowArea;

                double volume = length * width * height;
                double totalSurfaceArea = wallArea + roofArea + floorArea;
                double surfaceToVolumeRatio = totalSurfaceArea / volume;

                double tempDiff = Math.Abs(desiredTemp - externalTemp);

                double wallHeatLoss = opaqueWallArea * wallUValue * tempDiff;
                double windowHeatLoss = windowArea * windowUValue * tempDiff;
                double roofHeatLoss = roofArea * roofUValue * tempDiff;
                double floorHeatLoss = floorArea * floorUValue * tempDiff;

                double totalHeatLoss = wallHeatLoss + windowHeatLoss + roofHeatLoss + floorHeatLoss;

                double totalGains = internalGains + solarGains;
                double netHeating = totalHeatLoss - totalGains;

                double heatLossPerM2 = totalHeatLoss / floorArea;

                double recommendedWallU = wallUValue;
                double additionalInsulation = 0.0;
                Box optimisedRoom = room;

                if (targetHeatLoss > 0 && optimise)
                {
                    //First, calculate what wall U-value is needed to achieve temperature target
                    double targetWallLoss = targetHeatLoss - windowHeatLoss - roofHeatLoss - floorHeatLoss;

                    if (targetWallLoss > 0)
                    {
                        recommendedWallU = targetWallLoss / (opaqueWallArea * tempDiff);

                        // Estimate additional insulation thickness needed - for insulation with conductivity λ ≈ 0.035 W/mK
                        double currentR = 1.0 / wallUValue;
                        double targetR = 1.0 / recommendedWallU;
                        double additionalR = targetR - currentR;

                        if (additionalR > 0)
                        {
                            double insulationConductivity = 0.035;
                            additionalInsulation = additionalR * insulationConductivity * 1000;

                            //Optimised room with thicker walls, offset the box inwards
                            double offset = additionalInsulation / 1000.0;

                            Point3d center = room.Center;
                            Plane basePlane = room.Plane;

                            Interval xInterval = new Interval(-length / 2 - offset, length / 2 + offset);
                            Interval yInterval = new Interval(-width / 2 - offset, width / 2 + offset);
                            Interval zInterval = new Interval(-height / 2, height / 2 + offset);

                            optimisedRoom = new Box(basePlane, xInterval, yInterval, zInterval);
                        }
                    }
                    else
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                            "Target eat loss is too low - even perfect insulation cannot achieve this. Reduce windows or target.");
                    }
                }

                string report = $"===THERMAL PERFORMANCE ANALYSIS ===\n\n";
                report += $"GEOMETRY:\n";
                report += $"  Dimensions: {length:F2}m × {width:F2}m × {height:F2}m\n";
                report += $"  Floor area: {floorArea:F2} m²\n";
                report += $"  Volume: {volume:F2} m³\n";
                report += $"  Surface/Volume ratio: {surfaceToVolumeRatio:F3}\n\n";

                report += $"HEAT LOSS BREAKDOWN:\n";
                report += $"  Walls: {wallHeatLoss:F0} W ({wallHeatLoss / totalHeatLoss * 100:F1}%)\n";
                report += $"  Windows: {windowHeatLoss:F0} W ({windowHeatLoss / totalHeatLoss * 100:F1}%)\n";
                report += $"  Roof: {roofHeatLoss:F0} W ({roofHeatLoss / totalHeatLoss * 100:F1}%)\n";
                report += $"  Floor: {floorHeatLoss:F0} W ({floorHeatLoss / totalHeatLoss * 100:F1}%)\n";
                report += $"  TOTAL: {totalHeatLoss:F0} W\n\n";

                if (targetHeatLoss > 0 && optimise)
                {
                    report += $"OPTIMIZATION:\n";
                    report += $"  Target heat loss: {targetHeatLoss:F0} W\n";
                    report += $"  Current wall U-value: {wallUValue:F3} W/m²K\n";
                    report += $"  Required wall U-value: {recommendedWallU:F3} W/m²K\n";
                    report += $"  Additional insulation: {additionalInsulation:F0} mm\n";

                    if (recommendedWallU < wallUValue)
                    {
                        double savings = ((totalHeatLoss - targetHeatLoss) / totalHeatLoss) * 100;
                        report += $"  Heat loss reduction: {savings:F1}%\n";
                    }
                }
                else if (targetHeatLoss > 0)
                {
                    report += $"\nSet 'Optimise' to True to generate optimised geometry\n";
                }

                DA.SetData(0, totalHeatLoss);
                DA.SetData(1, netHeating);
                DA.SetData(2, wallHeatLoss);
                DA.SetData(3, windowHeatLoss);
                DA.SetData(4, roofHeatLoss);
                DA.SetData(5, floorHeatLoss);
                DA.SetData(6, surfaceToVolumeRatio);
                DA.SetData(7, heatLossPerM2);
                DA.SetData(8, recommendedWallU);
                DA.SetData(9, additionalInsulation);
                DA.SetData(10, optimisedRoom);
                DA.SetData(11, report);

                if (netHeating < 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                       "Gains exceed losses - cooling may be needed");
                }

                if (heatLossPerM2 > 100)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "High heat loss per m² - consider better insulation");
                }
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }

        protected override System.Drawing.Bitmap Icon => null;

        public override Guid ComponentGuid => new Guid("a6821c5e-d707-4172-af9d-9c0bee028c37");
    }
}