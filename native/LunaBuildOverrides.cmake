include(FetchContent)
# Override the upstream moving branch through FetchContent's first declaration.
FetchContent_Declare(minhook
    GIT_REPOSITORY https://github.com/TsudaKageyu/minhook
    GIT_TAG 8af6b4acae5a9388fd742b56fa79ece89d96f823
)
