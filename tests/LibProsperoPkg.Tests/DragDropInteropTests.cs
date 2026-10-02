using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Xunit;

namespace LibProsperoPkg.Tests;

public class DragDropInteropTests
{
    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string className);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string selectorName);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr class_getInstanceMethod(IntPtr cls, IntPtr sel);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr method_getImplementation(IntPtr method);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_arg(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_str(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern ulong objc_msgSend_ulong(IntPtr receiver, IntPtr selector);

    [DllImport("/System/Library/Frameworks/WebKit.framework/WebKit")]
    private static extern void fake_webkit_load();

    [Fact]
    public void TestWebKitAndCocoaSymbolsLoaded()
    {
        NativeLibrary.Load("/System/Library/Frameworks/Cocoa.framework/Cocoa");
        NativeLibrary.Load("/System/Library/Frameworks/WebKit.framework/WebKit");

        IntPtr wkClass = objc_getClass("WKWebView");
        Assert.NotEqual(IntPtr.Zero, wkClass);

        IntPtr selPerformDrop = sel_registerName("performDragOperation:");
        Assert.NotEqual(IntPtr.Zero, selPerformDrop);

        IntPtr method = class_getInstanceMethod(wkClass, selPerformDrop);
        Assert.NotEqual(IntPtr.Zero, method);

        IntPtr imp = method_getImplementation(method);
        Assert.NotEqual(IntPtr.Zero, imp);
    }

    [Fact]
    public void TestNSStringAndPasteboardInterop()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        IntPtr nsStringCls = objc_getClass("NSString");
        Assert.NotEqual(IntPtr.Zero, nsStringCls);

        IntPtr selStrWithUtf8 = sel_registerName("stringWithUTF8String:");
        IntPtr selUtf8 = sel_registerName("UTF8String");

        string testStr = "/tmp/test/folder/example";
        IntPtr nsStr = objc_msgSend_str(nsStringCls, selStrWithUtf8, testStr);
        Assert.NotEqual(IntPtr.Zero, nsStr);

        IntPtr cStr = objc_msgSend(nsStr, selUtf8);
        string? result = Marshal.PtrToStringUTF8(cStr);
        Assert.Equal(testStr, result);
    }

    [Fact]
    public void TestNSURLFileUrlParsing()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        IntPtr nsUrlCls = objc_getClass("NSURL");
        Assert.NotEqual(IntPtr.Zero, nsUrlCls);

        IntPtr selFileUrlWithPath = sel_registerName("fileURLWithPath:");
        IntPtr selPath = sel_registerName("path");
        IntPtr selUtf8 = sel_registerName("UTF8String");

        IntPtr nsStringCls = objc_getClass("NSString");
        IntPtr selStrWithUtf8 = sel_registerName("stringWithUTF8String:");
        IntPtr pathNsStr = objc_msgSend_str(nsStringCls, selStrWithUtf8, "/Applications/Utilities");

        IntPtr urlObj = objc_msgSend_arg(nsUrlCls, selFileUrlWithPath, pathNsStr);
        Assert.NotEqual(IntPtr.Zero, urlObj);

        IntPtr returnedPathNsStr = objc_msgSend(urlObj, selPath);
        Assert.NotEqual(IntPtr.Zero, returnedPathNsStr);

        IntPtr cStr = objc_msgSend(returnedPathNsStr, selUtf8);
        string? resolvedPath = Marshal.PtrToStringUTF8(cStr);
        Assert.Equal("/Applications/Utilities", resolvedPath);
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_args2(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_at(IntPtr receiver, IntPtr selector, ulong index);

    public static List<string> ExtractPathsFromPasteboard(IntPtr pasteboard)
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

    [Fact]
    public void TestPasteboardExtraction()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        NativeLibrary.Load("/System/Library/Frameworks/Cocoa.framework/Cocoa");
        IntPtr nsPasteboardCls = objc_getClass("NSPasteboard");
        Assert.NotEqual(IntPtr.Zero, nsPasteboardCls);

        IntPtr selGeneralPasteboard = sel_registerName("generalPasteboard");
        IntPtr pb = objc_msgSend(nsPasteboardCls, selGeneralPasteboard);
        Assert.NotEqual(IntPtr.Zero, pb);

        var extracted = ExtractPathsFromPasteboard(pb);
        // Should not crash and return a list
        Assert.NotNull(extracted);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NSPoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NSSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NSRect
    {
        public NSPoint Origin;
        public NSSize Size;
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern NSPoint objc_msgSend_point(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern NSRect objc_msgSend_rect(IntPtr receiver, IntPtr selector);

    [Fact]
    public void TestMouseLocationWithNSPoint()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        IntPtr nsEventCls = objc_getClass("NSEvent");
        Assert.NotEqual(IntPtr.Zero, nsEventCls);

        IntPtr selMouseLoc = sel_registerName("mouseLocation");
        NSPoint pt = objc_msgSend_point(nsEventCls, selMouseLoc);

        // Current mouse location on screen should have valid coordinates (finite numbers)
        Assert.False(double.IsNaN(pt.X));
        Assert.False(double.IsNaN(pt.Y));
    }

    [Fact]
    public void TestScreenFrameWithNSRect()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        IntPtr nsScreenCls = objc_getClass("NSScreen");
        Assert.NotEqual(IntPtr.Zero, nsScreenCls);

        IntPtr selMainScreen = sel_registerName("mainScreen");
        IntPtr mainScreen = objc_msgSend(nsScreenCls, selMainScreen);
        Assert.NotEqual(IntPtr.Zero, mainScreen);

        IntPtr selFrame = sel_registerName("frame");
        NSRect frame = objc_msgSend_rect(mainScreen, selFrame);

        Assert.True(frame.Size.Width > 0);
        Assert.True(frame.Size.Height > 0);
    }
}
