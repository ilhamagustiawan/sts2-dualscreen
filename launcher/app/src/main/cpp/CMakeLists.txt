cmake_minimum_required(VERSION 3.22.1)
project(sts2ds C)

# libsts2ds: the second-screen bridge. Java hands it the bottom screen's
# Surface and its touches; the game's C# side pushes frames and drains touches
# through P/Invoke. perf_hint.c: Android's performance hint (ADPF) for the
# game thread, driven from C# (DsPerfHint).
add_library(sts2ds SHARED sts2ds.c perf_hint.c)
target_compile_options(sts2ds PRIVATE -O2 -Wall -Wextra -fvisibility=hidden)
target_link_libraries(sts2ds android log dl)
