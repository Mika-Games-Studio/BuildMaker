#!/usr/bin/env bash
#
# Monta o .deb do BuildMaker para Ubuntu.
#
# Roda no Linux, e e o que o workflow de release chama no ubuntu-latest. Precisa
# do dotnet e do dpkg-deb; os dois ja vem na imagem do GitHub Actions.
#
# ONDE CADA COISA FICA, E POR QUE
#
#   /opt/buildmaker/buildmaker          o programa
#   /usr/bin/buildmaker                 link, para estar no PATH
#   /etc/buildmaker/appsettings.json    configuracao (conffile)
#   /lib/systemd/system/buildmaker.service
#
# /opt e nao /usr/bin direto porque o binario e self-contained: sao 40 MB com o
# runtime .NET dentro, e a convencao do Debian para software que traz as
# proprias dependencias e /opt. O link em /usr/bin e o que faz 'buildmaker'
# funcionar sem caminho completo.
#
# A configuracao fica em /etc e e declarada como conffile: assim o dpkg NAO a
# sobrescreve numa atualizacao. Sem isso, cada 'apt upgrade' apagaria a
# configuracao de quem usa o programa.
#
# uso:  tools/empacotar-deb.sh [versao] [arquitetura]

set -euo pipefail

VERSAO="${1:-1.0.0}"
ARCH="${2:-amd64}"

case "$ARCH" in
    amd64) RID="linux-x64"   ;;
    arm64) RID="linux-arm64" ;;
    *) echo "arquitetura desconhecida: $ARCH" >&2; exit 1 ;;
esac

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TRABALHO="$(mktemp -d)"
trap 'rm -rf "$TRABALHO"' EXIT

PACOTE="$TRABALHO/buildmaker"
SAIDA="$RAIZ/publicado-deb"

passo() { printf '\n== %s\n' "$1"; }

# ------------------------------------------------------------------ publicar

passo "Publicando para $RID"

dotnet publish "$RAIZ/src/UnityLocalCI.Cli/UnityLocalCI.Cli.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:Version="$VERSAO" \
    -o "$TRABALHO/publicado" \
    --nologo

# ------------------------------------------------------------------- arvore

passo "Montando a arvore do pacote"

install -d "$PACOTE/DEBIAN"
install -d "$PACOTE/opt/buildmaker"
install -d "$PACOTE/usr/bin"
install -d "$PACOTE/etc/buildmaker"
install -d "$PACOTE/lib/systemd/system"
install -d "$PACOTE/usr/share/doc/buildmaker"

install -m 755 "$TRABALHO/publicado/buildmaker" "$PACOTE/opt/buildmaker/buildmaker"
ln -s /opt/buildmaker/buildmaker "$PACOTE/usr/bin/buildmaker"

install -m 644 "$RAIZ/src/UnityLocalCI.Worker/appsettings.json" \
    "$PACOTE/etc/buildmaker/appsettings.json"

for doc in README.md TROUBLESHOOTING.md; do
    [ -f "$RAIZ/$doc" ] && install -m 644 "$RAIZ/$doc" "$PACOTE/usr/share/doc/buildmaker/"
done

TAMANHO_KB="$(du -sk "$PACOTE" | cut -f1)"

# ------------------------------------------------------------------ systemd

# Type=simple e nao notify: o .NET so precisa do notify para o protocolo de
# sd_notify, que este servico nao usa. Restart=on-failure e nao always, para uma
# configuracao invalida (que sai com codigo 1) nao virar laco de reinicio.
#
# O ExecStartPre e o que faz a configuracao quebrada aparecer no systemctl
# status como lista de problemas, em vez de um servico que morre sem dizer nada.
cat > "$PACOTE/lib/systemd/system/buildmaker.service" <<'UNIDADE'
[Unit]
Description=BuildMaker - CI local para projetos Unity
Documentation=https://github.com/Mikael-Cavalcanti/BuildMaker
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStartPre=/opt/buildmaker/buildmaker verificar
ExecStart=/opt/buildmaker/buildmaker servico
Restart=on-failure
RestartSec=10

# O servico roda como o usuario buildmaker, criado no postinst. Rodar como root
# daria ao Unity e ao git, que sao processos filhos, poder de root na maquina —
# e eles nao precisam de nada disso.
User=buildmaker
Group=buildmaker

# A pasta de trabalho dos projetos e grande e fica fora do pacote; o servico
# precisa poder escrever nela. StateDirectory cria /var/lib/buildmaker com o
# dono certo.
StateDirectory=buildmaker
WorkingDirectory=/var/lib/buildmaker

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=false

[Install]
WantedBy=multi-user.target
UNIDADE

# ------------------------------------------------------------------ controle

cat > "$PACOTE/DEBIAN/control" <<CONTROLE
Package: buildmaker
Version: $VERSAO
Section: devel
Priority: optional
Architecture: $ARCH
Maintainer: BSA Tech <mikael.cavalcanti@bsatech.io>
Depends: git, ca-certificates
Installed-Size: $TAMANHO_KB
Description: CI local para projetos Unity
 Observa a branch de homologacao de um projeto Unity, builda com o editor
 instalado na maquina, compacta e publica o zip numa pasta.
 .
 Sem nuvem, sem servidor HTTP e sem painel: tudo acontece na propria maquina.
 Esta e a versao de linha de comando, para servidor; a versao com janela existe
 so para Windows.
CONTROLE

# Sem isto o apt upgrade sobrescreve a configuracao de quem usa o programa.
cat > "$PACOTE/DEBIAN/conffiles" <<'CONF'
/etc/buildmaker/appsettings.json
CONF

cat > "$PACOTE/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e

if [ "$1" = "configure" ]; then
    # Usuario de sistema, sem shell e sem home proprio: ele existe para ser dono
    # do processo e dos arquivos, e nao para alguem entrar com ele.
    if ! getent passwd buildmaker >/dev/null; then
        adduser --system --group --no-create-home \
                --home /var/lib/buildmaker \
                --gecos "BuildMaker" buildmaker
    fi

    # O cofre de credenciais e a configuracao sao do servico, e so dele: o
    # appsettings.json cita nomes de credencial, e os arquivos do cofre tem o
    # valor em texto claro.
    chown -R root:buildmaker /etc/buildmaker
    chmod 750 /etc/buildmaker
    chmod 640 /etc/buildmaker/appsettings.json

    install -d -o buildmaker -g buildmaker -m 750 /var/lib/buildmaker

    systemctl daemon-reload || true

    # O servico NAO e iniciado aqui. A configuracao recem-instalada vem com
    # placeholders, e subir um servico que sai com erro no primeiro segundo so
    # enche o journal. Quem instala configura e entao habilita.
    echo ""
    echo "BuildMaker instalado."
    echo ""
    echo "  1. edite     /etc/buildmaker/appsettings.json"
    echo "  2. confira   buildmaker verificar"
    echo "  3. ligue     systemctl enable --now buildmaker"
    echo ""
fi

exit 0
POSTINST

cat > "$PACOTE/DEBIAN/prerm" <<'PRERM'
#!/bin/sh
set -e

if [ "$1" = "remove" ]; then
    systemctl stop buildmaker >/dev/null 2>&1 || true
    systemctl disable buildmaker >/dev/null 2>&1 || true
fi

exit 0
PRERM

cat > "$PACOTE/DEBIAN/postrm" <<'POSTRM'
#!/bin/sh
set -e

systemctl daemon-reload >/dev/null 2>&1 || true

# Em 'purge' o dpkg ja removeu /etc/buildmaker por ser conffile. O que fica e
# /var/lib/buildmaker, com o banco e o historico — e ele fica de proposito:
# remover um pacote nao e motivo para apagar o historico de builds de ninguem.
if [ "$1" = "purge" ]; then
    echo "Os dados em /var/lib/buildmaker NAO foram apagados."
fi

exit 0
POSTRM

chmod 755 "$PACOTE/DEBIAN/postinst" "$PACOTE/DEBIAN/prerm" "$PACOTE/DEBIAN/postrm"

# ------------------------------------------------------------------ empacotar

passo "Empacotando"

mkdir -p "$SAIDA"
ARQUIVO="$SAIDA/buildmaker_${VERSAO}_${ARCH}.deb"

# --root-owner-group: sem isto os arquivos ficam com o dono de quem rodou o
# script, e o lintian reclama — num runner do Actions seria o usuario 'runner'.
dpkg-deb --build --root-owner-group "$PACOTE" "$ARQUIVO"

echo ""
echo "Pronto: $ARQUIVO ($(du -h "$ARQUIVO" | cut -f1))"
echo ""
dpkg-deb --info "$ARQUIVO" | sed 's/^/   /'
