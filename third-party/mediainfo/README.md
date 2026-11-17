# MediaInfo native dependency

WindowsFS uses `MediaInfo.Core.Native` 26.10.0 (MediaInfoLib 26.10), distributed
by https://github.com/yartat/MP-MediaInfo from the MediaArea project.
Native binaries are restored through NuGet, not committed to this repository.

The Windows package includes MediaInfoLib, OpenSSL, curl, libssh2 and Brotli.
MediaInfoLib also uses ZenLib, zlib and tinyxml2. Their upstream license notices
are included here and copied into application distributions.

Upstream sources for these notices:

- https://github.com/MediaArea/MediaInfoLib/blob/master/License.html
- https://github.com/MediaArea/ZenLib/blob/master/License.txt
- https://github.com/openssl/openssl/blob/master/LICENSE.txt
- https://github.com/curl/curl/blob/master/COPYING
- https://github.com/libssh2/libssh2/blob/master/COPYING
- https://github.com/google/brotli/blob/master/LICENSE
- https://github.com/madler/zlib/blob/master/LICENSE
- https://github.com/leethomason/tinyxml2/blob/master/LICENSE.txt

WindowsFS invokes the MediaInfo buffer API with read-only local streams. It does
not invoke MediaInfo's path/URL open API, command-line tools, export, or demux.
