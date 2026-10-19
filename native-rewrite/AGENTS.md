# Native rewrite requirements

- Preserve the original project outside this directory. Create a source backup before structural changes.
- The product name in the UI is 豆包VR字幕. Do not add slogans, implementation details, performance counters, or migration notes to product screens.
- Build using build.ps1, CMake with MSVC, and the .NET SDK. No vcpkg, Conan, npm, pip, or additional dependency manager.
- Native dependencies are downloaded directly from fixed upstream URLs, verified against dependencies.lock.json, and consumed from dependencies/. CMake must not download dependencies.
- Managed dependencies use exact NuGet versions and committed packages.lock.json. Normal builds use locked restore.
- All owned resources must use RAII: com_ptr for COM, smart pointers for C++ objects, managed containers for buffers, scoped wrappers for mappings and handles, SafeHandle/SafeBuffer for P/Invoke ownership.
- Do not declare raw pointers, allocate with new/delete/malloc/free, call Release manually, or keep borrowed addresses in core logic or tests.
- Unavoidable SDK pointer conversion, borrowed pointer arrays, and mapped-memory access belong only in core/platform.cpp. That file may borrow addresses for individual SDK calls and map scopes; it must not own memory using raw pointers or return borrowed addresses.
- The C ABI uses integer object IDs and references to fixed-layout structures. Object IDs must not encode C++ addresses. Reject stale IDs and invalid structure sizes.
- Build policy checks, compiler warnings as errors, GPU tests, and managed ABI/lifecycle tests are required before publishing. Record hardware-dependent checks separately.
