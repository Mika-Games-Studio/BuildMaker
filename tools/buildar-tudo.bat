@echo off
chcp 65001 >nul
rem Toca o arquivo de gatilho de todos os projetos habilitados.
rem O servico observa esses arquivos e enfileira uma build do HEAD de cada um.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0buildar-tudo.ps1" %*
