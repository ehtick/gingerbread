﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Gingerbread.Core;

namespace Gingerbread.Core
{
    public static class LayoutPatch
    {
        /// <summary>
        /// Modify line segments within the input list: lines.
        /// Modify the input dictWindow and dictDoor.
        /// Modify the input dictFloor, appending the void region as slab holes.
        /// Output list for dictGlazing and dictRoom.
        /// </summary>
        public static List<gbSeg> PatchPerimeter(List<gbSeg> lines, List<gbXYZ> hull, List<gbSeg> thisWall, List<gbSeg> thisCurtain, 
            List<Tuple<gbXYZ, string>> thisWindow, List<Tuple<gbXYZ, string>> thisDoor, List<List<List<gbXYZ>>> thisFloor, 
            double offsetIn, double offsetExt, bool patchVoid, 
            out List<gbSeg> glazings, out List<gbSeg> airwalls, out List<List<gbXYZ>> voidRegions)
        {
            glazings = new List<gbSeg>();
            airwalls = new List<gbSeg>();
            voidRegions = new List<List<gbXYZ>>();

            List<gbXYZ> contourIn = GBMethod.OffsetPoly(hull, -offsetIn)[0];
            RegionTessellate.SimplifyPoly(contourIn);
            contourIn.Add(contourIn[0]);
            List<gbXYZ> contourExt = GBMethod.OffsetPoly(hull, offsetExt)[0];
            RegionTessellate.SimplifyPoly(contourExt);
            contourExt.Add(contourExt[0]);

            List<gbSeg> hullEdges = GBMethod.GetClosedPoly(hull);

            List<gbSeg> wallLines = GBMethod.FlattenLines(thisWall);

            // back projection to hull
            List<gbSeg> chisels = new List<gbSeg>();

            for (int i = wallLines.Count - 1; i >= 0; i--)
            {
                gbXYZ start = wallLines[i].Start;
                gbXYZ end = wallLines[i].End;
                if (!GBMethod.IsPtInPoly(start, contourIn, true) &&
                    !GBMethod.IsPtInPoly(end, contourIn, true) &&
                    GBMethod.IsPtInPoly(start, contourExt, false) &&
                    GBMethod.IsPtInPoly(end, contourExt, false))
                {
                    foreach (gbSeg hullEdge in hullEdges)
                    {
                        wallLines[i] = GBMethod.SegExtensionToSeg(wallLines[i], hullEdge,
                            Properties.Settings.Default.tolPerimeter);
                    }
                    foreach (gbSeg hullEdge in hullEdges)
                    {
                        double gap = GBMethod.SegDistanceToSeg(wallLines[i], hullEdge,
                            out double overlap, out gbSeg proj);
                        if (proj != null && gap < Properties.Settings.Default.tolPerimeter && proj.Length > 0)
                        {
                            //projected to all hull edges
                            gbSeg newProj = GBMethod.SegProjection(wallLines[i], hullEdge, false, out double inDistance);
                            chisels.Add(newProj);
                        }
                    }
                }
            }

            // etching the glazing area
            foreach (gbSeg hullEdge in hullEdges)
                glazings.Add(hullEdge);
            foreach (gbSeg chisel in chisels)
            {
                glazings = GBMethod.EtchSegs(glazings, chisel, 0.01);
            }

            // the internal curtain wall not projected to the hull boundary
            // should be kept and acting as glazings (on interior surface)
            foreach (gbSeg curtain in thisCurtain)
            {
                if (GBMethod.IsSegInPoly(curtain, contourIn))
                    glazings.Add(curtain);
            }

            // pull windows to the hull
            for (int i = thisWindow.Count - 1; i >= 0; i--)
            {
                gbXYZ ptSurrogate = GBMethod.FlattenPt(thisWindow[i].Item1);
                if (!GBMethod.IsPtInPoly(ptSurrogate, contourIn, true) &&
                    GBMethod.IsPtInPoly(ptSurrogate, hull, false))
                {
                    gbXYZ translation = new gbXYZ();
                    foreach (gbSeg hullEdge in hullEdges)
                    {
                        double distance = GBMethod.PtDistanceToSeg(ptSurrogate, hullEdge, out gbXYZ p, out double s);
                        if (distance < Properties.Settings.Default.tolPerimeter)
                        {
                            translation = p - ptSurrogate;
                            break;
                        }
                    }
                    thisWindow[i] = new Tuple<gbXYZ, string>(
                        thisWindow[i].Item1 + translation, thisWindow[i].Item2);
                }
            }

            // pull floors to the hull
            List<gbSeg> voidBoundary = new List<gbSeg>();
            // general collection
            List<List<gbXYZ>> allPanels = new List<List<gbXYZ>>();
            List<List<gbXYZ>> inPanels = new List<List<gbXYZ>>();
            foreach (List<List<gbXYZ>> slabs in thisFloor)
            {
                foreach (List<gbXYZ> loop in slabs)
                {
                    if (!GBMethod.IsClockwise(loop))
                        allPanels.Add(loop);
                }
            }

            if (allPanels.Count > 0)
            {
                foreach (List<gbXYZ> panel in allPanels)
                {
                    List<List<gbXYZ>> inPanel = GBMethod.ClipPoly(panel, hull, ClipperLib.ClipType.ctIntersection);
                    if (inPanel.Count > 0)
                        if (inPanel[0].Count > 0)
                            inPanels = GBMethod.ClipPoly(inPanels, inPanel[0], ClipperLib.ClipType.ctUnion);
                }

                for (int i = 0; i < inPanels.Count; i++)
                {
                    for (int j = 0; j < inPanels[i].Count; j++)
                    {
                        if (GBMethod.IsPtInPoly(inPanels[i][j], hull, true))
                        {
                            foreach (gbSeg hullEdge in hullEdges)
                            {
                                double distance = GBMethod.PtDistanceToSeg(inPanels[i][j], hullEdge, out gbXYZ p, out double s);
                                if (distance < Properties.Settings.Default.tolPerimeter)
                                {
                                    inPanels[i][j] = p;
                                }
                            }
                        }
                    }
                }

                List<List<gbXYZ>> outBand = GBMethod.ClipPoly(contourExt, hull, ClipperLib.ClipType.ctDifference);
                List<List<gbXYZ>> mergedPanel = GBMethod.ClipPoly(inPanels, outBand, ClipperLib.ClipType.ctUnion);
                foreach (List<gbXYZ> loop in mergedPanel)
                {
                    if (GBMethod.IsClockwise(loop))
                    {
                        double area = GBMethod.GetPolyArea(loop);
                        double perimeter = GBMethod.GetPolyPerimeter(loop);
                        if (area > 10 && perimeter / Math.Sqrt(area) < 6 || area > 100)
                        {
                            List<gbSeg> _airWalls = GBMethod.GetClosedPoly(loop);
                            List<gbSeg> clipperWalls = new List<gbSeg>();

                            for (int i = 0; i < _airWalls.Count; i++)
                            {
                                for (int j = 0; j < wallLines.Count; j++)
                                {
                                    gbSeg projection = GBMethod.SegProjection(wallLines[j], _airWalls[i], false, out double distance);
                                    if (projection.Length > 0.5 && distance < 2 * Properties.Settings.Default.tolAlignment)
                                        clipperWalls.Add(projection);
                                }
                            }
                            clipperWalls.AddRange(hullEdges);
                            foreach (gbSeg clipperWall in clipperWalls)
                                _airWalls = GBMethod.EtchSegs(_airWalls, clipperWall, 0.01);
                            airwalls.AddRange(_airWalls);
                            voidBoundary.AddRange(GBMethod.GetClosedPoly(loop));
                            //gbXYZ voidCentroid = GBMethod.GetPolyCentroid(loop);
                            voidRegions.Add(loop);
                        }
                    }
                }
            }


            for (int i = lines.Count - 1; i >= 0; i--)
            {
                gbXYZ start = lines[i].Start;
                gbXYZ end = lines[i].End;
                if (GBMethod.IsPtInPoly(start, hull, true) && GBMethod.IsPtInPoly(end, hull, true))
                {
                    if (!GBMethod.IsSegPolyIntersected(lines[i], contourIn, 0.000001) &&
                        !(GBMethod.IsPtInPoly(start, contourIn, false) || GBMethod.IsPtInPoly(end, contourIn, false)))
                    {
                        //lineBlocks[b][g].RemoveAt(i);
                        for (int j = 0; j < hull.Count - 1; j++)
                        {
                            gbSeg hullEdge = new gbSeg(hull[j], hull[j + 1]);
                            if (hullEdge.Length < 0.0001)
                                continue;
                            //segIntersectEnum result = GBMethod.SegIntersection(hullEdge, lineBlocks[b][g][i], 
                            //    0.000001, out gbXYZ intersection, out double t1, out double t2);
                            double gap = GBMethod.SegDistanceToSeg(lines[i], hullEdge,
                                out double overlap, out gbSeg proj);
                            if (proj != null && gap < Properties.Settings.Default.tolPerimeter && proj.Length > 0.5)
                            {
                                //Debug.Print($"LayoutPatch:: Original inside seg removed {lines[i]} gap-{gap} shadow-{proj.Length}");
                                //lineBlocks[b][g][i] = proj;
                                lines.RemoveAt(i);
                                break;
                            }
                        }
                    }
                    else
                    {
                        for (int j = 0; j < hull.Count - 1; j++)
                        {
                            GBMethod.SegExtendToSeg(lines[i], new gbSeg(hull[j], hull[j + 1]),
                                Properties.Settings.Default.tolDouble, Properties.Settings.Default.tolPerimeter);
                        }
                    }
                }
                else if (!GBMethod.IsSegPolyIntersected(lines[i], hull, 0.000001))
                {
                    lines.RemoveAt(i);
                    //Debug.Print($"LayoutPatch:: Original outside seg removed {lines[i]}");
                }
            }

            // patch the hull to the lineBlock to ensure there is no leakage
            // remember to fuse all the lines
            lines.AddRange(hullEdges);

            //for (int i = 0; i < lineBlocks[b][g].Count; i++)
            //    for (int j = 0; j < lineBlocks[b][g].Count; j++)
            //        if (i != j)
            //            lineBlocks[b][g][i] = GBMethod.SegExtension(lineBlocks[b][g][i], lineBlocks[b][g][j],
            //                Properties.Settings.Default.tolExpand);

            // patch the void boundary
            if (patchVoid)
                lines.AddRange(voidBoundary);

            // fuse the center lines
            return GBMethod.SegsFusion(lines, 0.01);
        }

        private static List<gbXYZ> SortPtLoop(List<gbSeg> lines)
        {
            if (lines.Count == 0)
            {
                //Rhino.RhinoApp.WriteLine("NO CURVE AS INPUT");
                return new List<gbXYZ>();
            }
            List<gbXYZ> ptLoop = new List<gbXYZ>();

            // first define the start point
            // all curve in crvs are shuffled up with random directions
            // but the start point has to be the joint of the first and last curve
            gbXYZ startPt = new gbXYZ();
            if (lines[0].PointAt(0) == lines.Last().PointAt(0) ||
              lines[0].PointAt(0) == lines.Last().PointAt(1))
                startPt = lines[0].PointAt(0);
            else
                startPt = lines[0].PointAt(1);

            ptLoop.Add(startPt);

            for (int i = 0; i < lines.Count; i++)
                if (lines[i].PointAt(0) == ptLoop[i])
                    ptLoop.Add(lines[i].PointAt(1));
                else
                    ptLoop.Add(lines[i].PointAt(0));

            return ptLoop;
        }                     

        /// <summary>
        /// </summary>
        public static void ColumnPatch(List<gbSeg> walls, List<List<List<gbXYZ>>> floors)
        {
            return;
        }
    }
}
