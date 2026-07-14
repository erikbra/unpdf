# frozen_string_literal: true

# NativeAOT PDF-to-HTML command-line converter.
class Unpdf < Formula
  desc "Convert PDF documents to semantic HTML"
  homepage "https://github.com/erikbra/pdfbox-net"
  version "4.0.0-preview.1"
  license "Apache-2.0"

  on_macos do
    on_arm do
      url "https://github.com/erikbra/unpdf/releases/download/unpdf-v4.0.0-preview.1/unpdf-4.0.0-preview.1-osx-arm64.tar.gz"
      sha256 "184f0f18a6e988c7d0ccec92c0c02464b8e9bd645a1518c1799c428ae2a4e5a3"
    end
    on_intel do
      url "https://github.com/erikbra/unpdf/releases/download/unpdf-v4.0.0-preview.1/unpdf-4.0.0-preview.1-osx-x64.tar.gz"
      sha256 "4ceed6aa9aa516c5051f2f5248910257f5634e863a5f54a6872ee50f446d3ea9"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/erikbra/unpdf/releases/download/unpdf-v4.0.0-preview.1/unpdf-4.0.0-preview.1-linux-arm64.tar.gz"
      sha256 "d95ebf121aa7d0c2751d04d24e38dff5e96d0d30ca40f771cfb04f4ce628415e"
    end
    on_intel do
      url "https://github.com/erikbra/unpdf/releases/download/unpdf-v4.0.0-preview.1/unpdf-4.0.0-preview.1-linux-x64.tar.gz"
      sha256 "0c9b7e2c338559425b1aa9e9b6b9d82b9c7fd965e2f0ab71900a40c296c27058"
    end
  end

  def install
    bin.install "unpdf"
    pkgshare.install "LICENSE.txt", "NOTICE.txt", "SIGNING.md", "VERSION", "artifact-manifest.json", "sbom.spdx.json"
  end

  test do
    require "base64"

    assert_match version.to_s, shell_output("#{bin}/unpdf --version")
    fixture = [
      "JVBERi0xLjQKJeLjz9MKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMC",
      "BvYmoKPDwgL1R5cGUgL1BhZ2VzIC9Db3VudCAxIC9LaWRzIFszIDAgUl0gPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5",
      "cGUgL1BhZ2UgL1BhcmVudCAyIDAgUiAvTWVkaWFCb3ggWzAgMCAzMDAgMzAwXSAvQ29udGVudHMgNCAwIFIgPj4KZW",
      "5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCAzNyA+PgpzdHJlYW0KQlQKL0YxIDEyIFRmCjcyIDcyMCBUZAooSGVsbG8p",
      "IFRqCkVUCmVuZHN0cmVhbQplbmRvYmoKNSAwIG9iago8PCAvVGl0bGUgKENsYXNzaWMgRml4dHVyZSkgL0F1dGhvci",
      "AocGRmYm94LW5ldCkgPj4KZW5kb2JqCnhyZWYKMCA2CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDAxNSAwMDAw",
      "MCBuIAowMDAwMDAwMDY0IDAwMDAwIG4gCjAwMDAwMDAxMjEgMDAwMDAgbiAKMDAwMDAwMDIwOCAwMDAwMCBuIAowMD",
      "AwMDAwMjk0IDAwMDAwIG4gCnRyYWlsZXIKPDwgL1NpemUgNiAvUm9vdCAxIDAgUiAvSW5mbyA1IDAgUiA+PgpzdGFy",
      "dHhyZWYKMzYxCiUlRU9GCg==",
    ].join
    (testpath/"fixture.pdf").binwrite(Base64.decode64(fixture))
    system bin/"unpdf", "fixture.pdf", "--output", "html", "--quiet"
    assert_match "Hello", (testpath/"html/index.html").read
  end
end
