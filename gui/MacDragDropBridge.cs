using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LibProsperoPkgGui;

internal static class MacDragDropBridge
{
    public delegate void FilesDroppedHandler(IReadOnlyList<string> paths, double x, double y);

    private static FilesDroppedHandler? _callback;
    private static PerformDragOperationDelegate? _swizzledDelegate; // Kept alive to prevent GC
    private static PerformDragOperationDelegate? _origPerformDragOp;
    private static bool _initialized;

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public NSPoint Origin;
        public NSSize Size;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte PerformDragOperationDelegate(IntPtr self, IntPtr cmd, IntPtr sender);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string className);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string selectorName);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr class_getInstanceMethod(IntPtr cls, IntPtr sel);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr method_setImplementation(IntPtr method, IntPtr imp);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_arg(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_args2(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_at(IntPtr receiver, IntPtr selector, ulong index);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_str(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern ulong objc_msgSend_ulong(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern NSPoint objc_msgSend_point(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern NSRect objc_msgSend_rect(IntPtr receiver, IntPtr selector);

    public static void Initialize(FilesDroppedHandler callback)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        if (_initialized)
        {
            _callback = callback;
            return;
        }

        try
        {
            _callback = callback;

            // Ensure Cocoa and WebKit are dynamically loaded
            NativeLibrary.Load("/System/Library/Frameworks/Cocoa.framework/Cocoa");
            NativeLibrary.Load("/System/Library/Frameworks/WebKit.framework/WebKit");

            IntPtr wkClass = objc_getClass("WKWebView");
            if (wkClass == IntPtr.Zero)
            {
                Console.Error.WriteLine("[MacDragDropBridge] WKWebView class not found");
                return;
            }

            IntPtr selPerformDrop = sel_registerName("performDragOperation:");
            IntPtr method = class_getInstanceMethod(wkClass, selPerformDrop);
            if (method == IntPtr.Zero)
            {
                Console.Error.WriteLine("[MacDragDropBridge] performDragOperation: method not found");
                return;
            }

            _swizzledDelegate = new PerformDragOperationDelegate(OnPerformDragOperation);
            IntPtr newImp = Marshal.GetFunctionPointerForDelegate(_swizzledDelegate);

            IntPtr oldImp = method_setImplementation(method, newImp);
            if (oldImp != IntPtr.Zero)
            {
                _origPerformDragOp = Marshal.GetDelegateForFunctionPointer<PerformDragOperationDelegate>(oldImp);
            }

            _initialized = true;
            Console.WriteLine("[MacDragDropBridge] Successfully hooked WKWebView performDragOperation:");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MacDragDropBridge] Initialization failed: {ex.Message}");
        }
    }

    private static byte OnPerformDragOperation(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try
        {
            if (sender != IntPtr.Zero)
            {
                IntPtr selPb = sel_registerName("draggingPasteboard");
                IntPtr pasteboard = objc_msgSend(sender, selPb);

                var paths = ExtractPaths(pasteboard);
                if (paths.Count > 0)
                {
                    double x = 0;
                    double y = 0;
                    try
                    {
                        IntPtr selLoc = sel_registerName("draggingLocation");
                        NSPoint pt = objc_msgSend_point(sender, selLoc);

                        IntPtr selWin = sel_registerName("draggingDestinationWindow");
                        IntPtr win = objc_msgSend(sender, selWin);
                        if (win != IntPtr.Zero)
                        {
                            IntPtr selFrame = sel_registerName("frame");
                            NSRect frame = objc_msgSend_rect(win, selFrame);
                            x = pt.X;
                            y = frame.Size.Height - pt.Y; // Convert to top-left web coordinate
                        }
                        else
                        {
                            x = pt.X;
                            y = pt.Y;
                        }
                    }
                    catch { }

                    _callback?.Invoke(paths, x, y);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MacDragDropBridge] Drop processing error: {ex.Message}");
        }

        byte result = 1;
        if (_origPerformDragOp != null)
        {
            try
            {
                result = _origPerformDragOp(self, cmd, sender);
            }
            catch
            {
                result = 1;
            }
        }

        return result != 0 ? result : (byte)1;
    }

    private static List<string> ExtractPaths(IntPtr pasteboard)
    {
        var paths = new List<string>();
        if (pasteboard == IntPtr.Zero) return paths;

        // Strategy 1: readObjectsForClasses:@[[NSURL class]] options:nil
        try
        {
            IntPtr nsArrayCls = objc_getClass("NSArray");
            IntPtr nsUrlCls = objc_getClass("NSURL");
            if (nsArrayCls != IntPtr.Zero && nsUrlCls != IntPtr.Zero)
            {
                IntPtr selArrayWithObject = sel_registerName("arrayWithObject:");
                IntPtr selReadObjects = sel_registerName("readObjectsForClasses:options:");
                IntPtr selCount = sel_registerName("count");
                IntPtr selObjectAtIndex = sel_registerName("objectAtIndex:");
                IntPtr selPath = sel_registerName("path");
                IntPtr selUtf8 = sel_registerName("UTF8String");

                IntPtr classesArray = objc_msgSend_arg(nsArrayCls, selArrayWithObject, nsUrlCls);
                if (classesArray != IntPtr.Zero)
                {
                    IntPtr urlsArray = objc_msgSend_args2(pasteboard, selReadObjects, classesArray, IntPtr.Zero);
                    if (urlsArray != IntPtr.Zero)
                    {
                        ulong count = objc_msgSend_ulong(urlsArray, selCount);
                        for (ulong i = 0; i < count; i++)
                        {
                            IntPtr url = objc_msgSend_at(urlsArray, selObjectAtIndex, i);
                            if (url != IntPtr.Zero)
                            {
                                IntPtr pathNsStr = objc_msgSend(url, selPath);
                                if (pathNsStr != IntPtr.Zero)
                                {
                                    IntPtr cStr = objc_msgSend(pathNsStr, selUtf8);
                                    if (cStr != IntPtr.Zero)
                                    {
                                        string? p = Marshal.PtrToStringUTF8(cStr);
                                        if (!string.IsNullOrEmpty(p) && !paths.Contains(p))
                                        {
                                            paths.Add(p);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        if (paths.Count > 0) return paths;

        // Strategy 2: propertyListForType:@"NSFilenamesPboardType"
        try
        {
            IntPtr nsStringCls = objc_getClass("NSString");
            IntPtr selStrWithUtf8 = sel_registerName("stringWithUTF8String:");
            IntPtr typeStr = objc_msgSend_str(nsStringCls, selStrWithUtf8, "NSFilenamesPboardType");
            IntPtr selPropertyList = sel_registerName("propertyListForType:");
            IntPtr selCount = sel_registerName("count");
            IntPtr selObjectAtIndex = sel_registerName("objectAtIndex:");
            IntPtr selUtf8 = sel_registerName("UTF8String");

            IntPtr list = objc_msgSend_arg(pasteboard, selPropertyList, typeStr);
            if (list != IntPtr.Zero)
            {
                ulong count = objc_msgSend_ulong(list, selCount);
                for (ulong i = 0; i < count; i++)
                {
                    IntPtr itemStr = objc_msgSend_at(list, selObjectAtIndex, i);
                    if (itemStr != IntPtr.Zero)
                    {
                        IntPtr cStr = objc_msgSend(itemStr, selUtf8);
                        if (cStr != IntPtr.Zero)
                        {
                            string? p = Marshal.PtrToStringUTF8(cStr);
                            if (!string.IsNullOrEmpty(p) && !paths.Contains(p))
                            {
                                paths.Add(p);
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return paths;
    }
}
