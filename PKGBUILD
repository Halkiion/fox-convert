# Maintainer: Halkion <halkion@yahoo.com>
pkgname=fox-convert-git
pkgver=0.0.0
pkgrel=1
pkgdesc="A tool to convert Chrome extensions to Firefox extensions"
arch=('x86_64')
url="https://github.com/Halkiion/fox-convert"
license=('MIT')
depends=()
makedepends=('git' 'dotnet-sdk')
source=("$pkgname::git+https://github.com/Halkiion/fox-convert.git")
sha256sums=('SKIP')

pkgver() {
  cd "$srcdir/$pkgname"
  git rev-list --count HEAD
}

build() {
  cd "$srcdir/$pkgname/fox-convert"
  dotnet publish -c Release -r linux-x64 --self-contained true -o publish-linux/
}

package() {
  install -Dm755 "$srcdir/$pkgname/fox-convert/publish-linux/fox-convert" "$pkgdir/usr/bin/fox-convert"
  install -Dm644 "$srcdir/$pkgname/LICENSE" "$pkgdir/usr/share/licenses/$pkgname/LICENSE"
  install -Dm644 "$srcdir/$pkgname/README.md" "$pkgdir/usr/share/doc/$pkgname/README.md"
}
