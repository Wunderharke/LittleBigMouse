﻿/*
  LittleBigMouse.Daemon
  Copyright (c) 2017 Mathieu GRENET.  All right reserved.

  This file is part of LittleBigMouse.Daemon.

    LittleBigMouse.Daemon is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    LittleBigMouse.Daemon is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with MouseControl.  If not, see <http://www.gnu.org/licenses/>.

	  mailto:mathieu@mgth.fr
	  http://www.mgth.fr
*/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.Threading;
using System.Windows;
using HLab.DependencyInjection.Annotations;
using HLab.Windows.API;
using HLab.Windows.Monitors;
using HLab.Windows.MonitorVcp;
using LittleBigMouse.ScreenConfigs;
using Microsoft.Win32;
using MouseHooker;
//using static HLab.Windows.API.NativeMethods;

namespace LittleBigMouse_Daemon
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
    public class MouseEngine
    {
        public ScreenConfig Config { get; private set; }

        private Zones _zones;

        private double _initMouseSpeed;
        //public readonly IMouseHooker Hook = new MouseHookerWinEvent();
        public readonly IMouseHooker Hook = new MouseHookerWindowsHook();

        private ServiceHost _host;

        public void StartServer(ILittleBigMouseService service)
        {
            if (_host == null)
            {
                _host = new ServiceHost(service, LittleBigMouseClient.Address);
                var smb = new ServiceMetadataBehavior
                {
                    MetadataExporter = { PolicyVersion = PolicyVersion.Policy15 }
                };

                _host.Description.Behaviors.Add(smb);
            }
            _host.Open();
        }

        public void StopServer()
        {
            _host.Close();
        }

        public void Quit()
        {
            throw new NotImplementedException("MouseEngine : Quit");
        }

        public void Start()
        {
            Stop();

            LoadConfig();

            if (Config == null || !Config.Enabled) return;

            using (RegistryKey key = ScreenConfig.OpenRootRegKey(true))
            {
                string ms = key.GetValue("InitialMouseSpeed", string.Empty).ToString();

                if (string.IsNullOrEmpty(ms))
                {
                    _initMouseSpeed = LbmMouse.MouseSpeed;
                    key.SetValue("InitialMouseSpeed", _initMouseSpeed.ToString(CultureInfo.InvariantCulture),
                        RegistryValueKind.String);
                }
                else
                    double.TryParse(ms, out _initMouseSpeed);

                using (RegistryKey savekey = key.CreateSubKey("InitialCursor"))
                {
                    if (savekey?.ValueCount == 0)
                    {
                        LbmMouse.SaveCursor(savekey);
                    }
                }
            }

            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;


            _handler = OnMouseMoveExtFirst;
            Hook.MouseMove += _handler;

            if (Config.AdjustPointer)
                ZoneChanged += AdjustPointer;

            if (Config.AdjustSpeed)
                ZoneChanged += AdjustSpeed;

            if (Config.HomeCinema)
                ZoneChanged += HomeCinema;

            Hook.Hook();
        }

        public void Stop()
        {
            // TODO : if (!Hook.Enabled) return;

            if (Config == null) return;

            Hook.MouseMove -= _handler;

            if (Config.AdjustPointer)
                ZoneChanged -= AdjustPointer;

            if (Config.AdjustSpeed)
                ZoneChanged -= AdjustSpeed;

            if (Config.HomeCinema)
                ZoneChanged -= HomeCinema;

            Hook.UnHook();

            if (Config == null) return;

            if (Config.AdjustSpeed)
            {
                LbmMouse.MouseSpeed = _initMouseSpeed;
                using (var key = ScreenConfig.OpenRootRegKey(true))
                {
                    key.DeleteValue("InitialMouseSpeed");
                }
            }

            if (Config.AdjustPointer)
            {
                using (var key = ScreenConfig.OpenRootRegKey())
                {
                    using (RegistryKey savekey = key.OpenSubKey("InitialCursor"))
                    {
                        if (savekey != null)
                        {
                            LbmMouse.RestoreCursor(savekey);
                        }
                    }
                    key.DeleteSubKey("InitialCursor");
                }
            }

            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;
        }


        public void LoadConfig(ScreenConfig config)
        {
            Config = config;
            ConfigLoaded?.Invoke(config, null);
        }

        public event EventHandler ConfigLoaded;

        private readonly IMonitorsService _monitorService;

        public void LoadConfig()
        {
            LoadConfig(new ScreenConfig(_monitorService));

            _zones = new Zones();
            foreach (var screen in Config.AllScreens)
            {
                _zones.Add(new Zone(screen));
            }


            if (Config.LoopX)
            {
                foreach (var screen in Config.AllScreens)
                {
                    var main = _zones.Main.FirstOrDefault(e => ReferenceEquals(e.Screen, screen));
                    _zones.Add(new Zone(screen, main, -Config.PhysicalOutsideBounds.Width, 0));
                    _zones.Add(new Zone(screen, main, Config.PhysicalOutsideBounds.Width, 0));
                }
            }

            if (Config.LoopY)
            {
                foreach (var screen in Config.AllScreens)
                {
                    var main = _zones.Main.FirstOrDefault(e => ReferenceEquals(e.Screen, screen));
                    _zones.Add(new Zone(screen, main, 0, -Config.PhysicalOutsideBounds.Height));
                    _zones.Add(new Zone(screen, main, 0, Config.PhysicalOutsideBounds.Height));
                }
            }
        }

        private readonly Stopwatch _timer = new Stopwatch();
        private int _count = -10;

        //private void OnMouseMoveExt(object sender, MouseEventExtArgs e)
        //{
        //    //_timer.Start();
        //    //try
        //    //{
        //    if (e.Clicked) return;
        //    var pIn = new Point(e.X, e.Y);

        //    if (_oldZone.ContainsPx(pIn))
        //    {
        //        _oldPoint = pIn;
        //        e.Handled = false;
        //        return;
        //    }

        //    e.Handled = _handler(pIn);

        //    //}
        //    //finally
        //    //{
        //    //    _timer.Stop();
        //    //    _count++;               
        //    //}
        //}

        private void PrintResult()
        {
            Console.WriteLine("AVG :" + _timer.ElapsedTicks / _count);
            Console.WriteLine("AVG :" + _timer.Elapsed.TotalMilliseconds / _count);
        }


        private EventHandler<HookMouseEventArg> _handler;
        private Point _oldPoint;
        private Zone _oldZone;

        [Import]
        public MouseEngine(IMonitorsService monitorService)
        {
            _monitorService = monitorService;
        }

        private void OnMouseMoveExtFirst(object sender, HookMouseEventArg e)
        {
            _oldPoint = e.Point; //new Point(e.X,e.Y);
            //_oldScreenRect = Config.ScreenFromPixel(_oldPoint).InPixel.Bounds;
            _oldZone = _zones.FromPx(_oldPoint);

            Hook.MouseMove -= _handler;

            if (Config.AllowCornerCrossing)
                _handler = MouseMoveCross;
            else
            {
                _handler = MouseMoveStraight;
            }

            Hook.MouseMove += _handler;

            //e.Handled = false;
        }

        public event EventHandler<ZoneChangeEventArgs> ZoneChanged;

        [DllImport("user32", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint dwFlags,
            bool fInherit,
            uint dwDesiredAccess);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetThreadDesktop(IntPtr hDesktop);
        [DllImport("user32.dll")]
        private static extern bool SwitchDesktop(IntPtr hDesktop);
        [DllImport("user32.dll")]
        public static extern IntPtr GetThreadDesktop(int dwThreadId);

        [DllImport("kernel32.dll")]
        public static extern int GetCurrentThreadId();
        enum DESKTOP_ACCESS : uint
        {
            DESKTOP_NONE = 0,
            DESKTOP_READOBJECTS = 0x0001,
            DESKTOP_CREATEWINDOW = 0x0002,
            DESKTOP_CREATEMENU = 0x0004,
            DESKTOP_HOOKCONTROL = 0x0008,
            DESKTOP_JOURNALRECORD = 0x0010,
            DESKTOP_JOURNALPLAYBACK = 0x0020,
            DESKTOP_ENUMERATE = 0x0040,
            DESKTOP_WRITEOBJECTS = 0x0080,
            DESKTOP_SWITCHDESKTOP = 0x0100,

            GENERIC_ALL = (DESKTOP_READOBJECTS | DESKTOP_CREATEWINDOW | DESKTOP_CREATEMENU |
                           DESKTOP_HOOKCONTROL | DESKTOP_JOURNALRECORD | DESKTOP_JOURNALPLAYBACK |
                           DESKTOP_ENUMERATE | DESKTOP_WRITEOBJECTS | DESKTOP_SWITCHDESKTOP),
        }
        [DllImport("user32.dll")]
        private static extern bool EnumWindowStations(EnumWindowStationsDelegate lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowStationsDelegate(string windowsStation, IntPtr lParam);

        private static bool EnumWindowStationsCallback(string windowStation, IntPtr lParam)
        {
            GCHandle gch = GCHandle.FromIntPtr(lParam);
            IList<string> list = gch.Target as List<string>;

            if (null == list)
            {
                return (false);
            }

            list.Add(windowStation);

            return (true);
        }
        private void MouseMoveStraight(object sender, HookMouseEventArg e)
        {
            //TODO : if (e.Clicked) return;
            var pIn = e.Point;

            if (_oldZone.ContainsPx(pIn))
            {
                _oldPoint = pIn;
                e.Handled = false;
                return;
            }


            //Point oldpInMm = _oldZone.Px2Mm(_oldPoint);
            Point pInMm = _oldZone.Px2Mm(pIn);
            Zone zoneOut = null;

            var minDx = 0.0;
            var minDy = 0.0;

            if (pIn.Y > _oldZone.Px.Bottom)
            {
                foreach (var zone in _zones.All/*.Where(z => z.Mm.Top > _oldZone.Mm.Bottom)*/)
                {
                    if (zone.Mm.Left > pInMm.X || zone.Mm.Right < pInMm.X) continue;
                    var dy = zone.Mm.Top - _oldZone.Mm.Bottom;

                    if (dy < 0) continue;
                    if (dy > minDy && minDy > 0) continue;


                    // = pInMm + new Vector(0, dy);
                    minDy = dy;
                    zoneOut = zone;
                }
            }
            else if (pIn.Y < _oldZone.Px.Top)
            {
                foreach (var zone in _zones.All/*.Where(z => !ReferenceEquals(z, _oldZone))*/)
                {
                    if (zone.Mm.Left > pInMm.X || zone.Mm.Right < pInMm.X) continue;
                    var dy = zone.Mm.Bottom - _oldZone.Mm.Top;

                    if (dy > 0) continue;
                    if (dy < minDy && minDy < 0) continue;

                    minDy = dy;
                    zoneOut = zone;
                }
            }

            if (pIn.X > _oldZone.Px.Right)
            {
                foreach (var zone in _zones.All)
                {
                    if (zone.Mm.Top > pInMm.Y || zone.Mm.Bottom < pInMm.Y) continue;
                    var dx = zone.Mm.Left - _oldZone.Mm.Right;

                    if (dx < 0) continue;
                    if (dx > minDx && minDx > 0) continue;

                    minDx = dx;
                    zoneOut = zone;
                }
            }
            else if (pIn.X < _oldZone.Px.Left)
            {
                foreach (var zone in _zones.All)
                {
                    if (zone.Mm.Top > pInMm.Y || zone.Mm.Bottom < pInMm.Y) continue;
                    var dx = zone.Mm.Right - _oldZone.Mm.Left;

                    if (dx < minDx && minDx < 0) continue;
                    if (dx > 0) continue;

                    minDx = dx;
                    zoneOut = zone;
                }
            }

            if (zoneOut == null)
            {
                LbmMouse.CursorPos = _oldZone.InsidePx(pIn);
                e.Handled = true;
                return;
            }

            var pOut = zoneOut.Mm2Px(new Point(pInMm.X + minDx, pInMm.Y + minDy));
            pOut = zoneOut.InsidePx(pOut);
            _oldZone = zoneOut.Main;
            _oldPoint = pOut;

            //IntPtr hwnd = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
            //SetThreadDesktop(hwnd);

            //var movement = new INPUT { Type = (UInt32)0 };
            //movement.Data.Mouse.Flags = (UInt32)(MouseFlag.Move | MouseFlag.Absolute | MouseFlag.VirtualDesk);
            //movement.Data.Mouse.X = (int)pOut.X;
            //movement.Data.Mouse.Y = (int)pOut.Y;

            //INPUT[] inputs = {movement};

            //SendInput((UInt32)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));

            LbmMouse.CursorPos = pOut;

            var p = LbmMouse.CursorPos;
            if (Math.Abs(pOut.X - p.X) >= 1 || Math.Abs(pOut.Y - p.Y) >= 1)
            {
                IntPtr hOldDesktop = GetThreadDesktop(GetCurrentThreadId());
                IntPtr hwnd = OpenInputDesktop(0, true, (uint)DESKTOP_ACCESS.GENERIC_ALL);

                Thread t = new Thread(() =>
                {
                    SwitchDesktop(hwnd);
                    var b = SetThreadDesktop(hwnd);

                    var b2 = LbmMouse.MouseEvent(
                        NativeMethods.MOUSEEVENTF.ABSOLUTE | NativeMethods.MOUSEEVENTF.MOVE
                        | NativeMethods.MOUSEEVENTF.VIRTUALDESK
                        , pOut.X, pOut.Y);
                    if (b2 == 0)
                    {
                        var s = NativeMethods.GetLastError();
                    }

                    //LbmMouse.CursorPos = pOut;
                    var b3 = NativeMethods.SetCursorPos((int)pOut.X, (int)pOut.Y);

                    if (b3 == false)
                    {
                        var s = NativeMethods.GetLastError();
                    }

                    //    IList<string> list = new List<string>();
                    //    GCHandle gch = GCHandle.Alloc(list);
                    //    EnumWindowStationsDelegate childProc = new EnumWindowStationsDelegate(EnumWindowStationsCallback);

                    //    EnumWindowStations(childProc, GCHandle.ToIntPtr(gch));


                    //}
                });

                t.Start();
                t.Join();

                SwitchDesktop(hOldDesktop);

                //var w = new Window
                //{
                //    WindowStyle = WindowStyle.None,
                //    Visibility = Visibility.Collapsed,
                //    Width = 0,
                //    Height = 0
                //};
                //w.Show();

                ////const int DESKTOP_SWITCHDESKTOP = 256;
                ////IntPtr hwnd = OpenInputDesktop(0, false, 0x00020000);
                ////var b = SetThreadDesktop(hwnd);

                //LbmMouse.CursorPos = pOut;

                //w.Close();
            }

            ZoneChanged?.Invoke(this, new ZoneChangeEventArgs(_oldZone, zoneOut));
            e.Handled = true;
        }

        private void MouseMoveCross(object sender, HookMouseEventArg e)
        {
            // TODO : if (e.Clicked) return;
            var pIn = e.Point;

            if (_oldZone.ContainsPx(pIn))
            {
                _oldPoint = pIn;
                e.Handled = false;
                return;
            }

            var pInMmOld = _oldZone.Px2Mm(_oldPoint);
            var pInMm = _oldZone.Px2Mm(pIn);
            Zone zoneOut = null;

            var trip = new Segment(pInMmOld, pInMm);
            var minDist = double.PositiveInfinity;

            var pOutInMm = pInMm;

            foreach (var zone in _zones.All.Where(z => !ReferenceEquals(z, _oldZone)))
            {
                foreach (var p in trip.Line.Intersect(zone.Mm))
                {
                    var travel = new Segment(pInMmOld, p);
                    if (!travel.Rect.Contains(pInMm)) continue;
                    var dist = travel.SizeSquared;
                    if (dist > minDist) continue;

                    minDist = dist;
                    zoneOut = zone;
                    pOutInMm = p;
                }
            }

            if (zoneOut == null)
            {
                LbmMouse.CursorPos = _oldZone.InsidePx(pIn);
                e.Handled = true;
                return;
            }

            var pOut = zoneOut.Mm2Px(pOutInMm);
            pOut = zoneOut.InsidePx(pOut);
            _oldZone = zoneOut.Main;
            _oldPoint = pOut;
            LbmMouse.CursorPos = pOut;
            ZoneChanged?.Invoke(this, new ZoneChangeEventArgs(_oldZone, zoneOut));
            e.Handled = true;
            return;

            //}

            //finally
            //{
            //    if (_count >= 0) _timer.Stop();
            //    _count++;
            //}
        }

        public class ZoneChangeEventArgs : EventArgs
        {
            public ZoneChangeEventArgs(Zone oldZone, Zone newZone)
            {
                OldZone = oldZone;
                NewZone = newZone;
            }

            public Zone OldZone { get; }
            public Zone NewZone { get; }
        }

        private void AdjustPointer(object sender, ZoneChangeEventArgs args)
        {
            if (args.NewZone.Dpi - args.OldZone.Dpi < 1) return;
            if (args.NewZone.Dpi > 110)
            {
                LbmMouse.SetCursorAero(args.NewZone.Dpi > 138 ? 3 : 2);
            }
            else LbmMouse.SetCursorAero(1);
        }

        private void AdjustSpeed(object sender, ZoneChangeEventArgs args)
        {
            LbmMouse.MouseSpeed = Math.Round((5.0 / 96.0) * args.NewZone.Dpi, 0);
        }

        private void HomeCinema(object sender, ZoneChangeEventArgs args)
        {
            args.OldZone.Screen.Monitor.Vcp().Power = false;
            args.NewZone.Screen.Monitor.Vcp().Power = true;
        }


        public void MatchConfig(string configId)
        {
            Config.MatchConfig(configId);
        }
    }
}

