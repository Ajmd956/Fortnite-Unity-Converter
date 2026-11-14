@echo off
cd /d "%~dp0update-tester"
if not exist node_modules call npm install --no-audit --no-fund
call npm start
