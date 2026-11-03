#include <windows.h>
#include <tlhelp32.h>
#include <psapi.h>
#include <shlwapi.h>
#include <atlbase.h>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <memory>
#include <cwchar>

// Compile the pinned upstream implementation without copying or changing it.
#include "dllinject.cpp"

int wmain(int argc, wchar_t* argv[])
{
    if (argc != 3 || !std::filesystem::path(argv[2]).is_absolute() ||
        !std::filesystem::is_regular_file(argv[2]))
        return 2;
    wchar_t* end = nullptr;
    const auto pid = std::wcstoul(argv[1], &end, 10);
    if (pid == 0 || !end || *end != L'\0')
        return 2;
    // The upstream entry point returns 1 on success.
    return dllinjectwmain(argc, argv) == 1 ? 0 : 1;
}
