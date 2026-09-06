const std = @import("std");

// One build script, callable identically from any host OS running `zig
// build` - Windows, Linux, or macOS - that produces all four target
// binaries (win-x64, linux-x64, osx-x64, osx-arm64) every time. Replaces
// hand-maintained per-OS scripts so every platform's binary always goes
// through the exact same logic instead of separate copies. See VERSION.txt
// for why MDBX_HAVE_BUILTIN_CPU_SUPPORTS=0 is needed on every target.
pub fn build(b: *std.Build) void {
    buildMdbx(b, .{ .cpu_arch = .x86_64, .os_tag = .windows, .abi = .gnu }, "win-x64", "mdbx.dll");
    buildMdbx(b, .{ .cpu_arch = .x86_64, .os_tag = .linux, .abi = .gnu }, "linux-x64", "libmdbx.so");
    buildMdbx(b, .{ .cpu_arch = .x86_64, .os_tag = .macos }, "osx-x64", "libmdbx.dylib");
    buildMdbx(b, .{ .cpu_arch = .aarch64, .os_tag = .macos }, "osx-arm64", "libmdbx.dylib");
}

fn buildMdbx(b: *std.Build, query: std.Target.Query, rid: []const u8, filename: []const u8) void {
    const target = b.resolveTargetQuery(query);

    const lib = b.addLibrary(.{
        .name = "mdbx",
        .linkage = .dynamic,
        .root_module = b.createModule(.{
            .target = target,
            .optimize = .ReleaseFast,
            .link_libc = true,
        }),
    });

    lib.root_module.addCSourceFile(.{
        .file = b.path("mdbx.c"),
        .flags = &.{
            "-DNDEBUG",
            "-DLIBMDBX_EXPORTS",
            "-DMDBX_BUILD_SHARED_LIBRARY=1",
            "-DMDBX_HAVE_BUILTIN_CPU_SUPPORTS=0",
            "-DMDBX_CONFIG_H=\"config.h\"",
        },
    });

    if (query.os_tag == .linux) {
        lib.root_module.addIncludePath(b.path("zig-shim"));
    }
    if (query.os_tag == .windows) {
        lib.root_module.linkSystemLibrary("ntdll", .{});
    }

    // would suggest: zig-out -> libmdbx -> vendor -> RhinoDB.Native.
    const dest_dir: std.Build.InstallDir = .{ .custom = b.fmt("../../../runtimes/{s}/native", .{rid}) };
    const install = b.addInstallFileWithDir(lib.getEmittedBin(), dest_dir, filename);
    b.getInstallStep().dependOn(&install.step);
}
