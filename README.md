# OpenAI Usage Widget

Miniapp de escritorio para Windows 11, escrita en **C# / WPF / .NET 10**. Muestra las cuotas de **ChatGPT/Codex**, no el gasto de la API de OpenAI.

- Varias cuentas, selector superior y acceso claro a **Agregar primera cuenta**.
- Cuota restante de 5 horas y semanal, con sus próximos restablecimientos.
- Saldo real de **reseteos disponibles**, obtenido de `rateLimitResetCredits.availableCount`.
- Refresco cada 5 minutos, manual para la cuenta seleccionada y de datos vencidos al reanudar Windows.
- Fecha/hora local del último refresco **exitoso por cuenta**. Un error no adelanta esa fecha ni borra la última lectura.
- Ventana sin bordes ni botón en la barra de tareas; se coloca inmediatamente por encima del escritorio y por debajo de las aplicaciones. No es un widget de Win+W.
- Icono de notificación para configuración, restaurar posición y salir. Inicio con Windows opcional, desactivado de fábrica.

## Ejecutar

Requiere Windows 11 de 64 bits y el **SDK de .NET 10** para desarrollar. No hace falta Visual Studio completo.

Desde PowerShell, en la raíz del repositorio:

```powershell
.\run.ps1
```

El script compila y abre la app sin mantener una consola adicional. También se puede compilar y abrir el ejecutable directamente:

```powershell
dotnet build OpenAIUsageWidget.slnx -c Release
.\src\OpenAIUsageWidget\bin\Release\net10.0-windows\OpenAIUsageWidget.exe
```

Desde Git Bash:

```bash
dotnet build OpenAIUsageWidget.slnx -c Release
./src/OpenAIUsageWidget/bin/Release/net10.0-windows/OpenAIUsageWidget.exe
```

Si recién instalaste el SDK, reiniciá VS Code para renovar el `PATH`, o usá `"/c/Program Files/dotnet/dotnet.exe"` en Git Bash.

Arrastrá una zona libre del panel para moverlo. La posición se guarda. Para cerrar, usá el icono del área de notificaciones → **Salir**. La ventana no se mantiene encima de las demás aplicaciones.

## Conectar cuentas reales

La interfaz y la demostración no requieren Codex. **Esta primera implementación de la consulta real sí necesita Codex CLI como conector local de autenticación/cuotas**. No es el agente que desarrolla el proyecto, no sustituye OpenCode y no necesita que OpenCode esté abierto.

Se reutiliza `codex app-server` para no copiar cookies del navegador, leer credenciales de otras aplicaciones ni implementar un OAuth con un cliente ajeno. El widget **no instala ni descarga el conector automáticamente**.

1. Si querés conectar cuentas, instalá [Codex CLI desde su documentación oficial](https://github.com/openai/codex#installing-and-running-codex-cli). Si ya tenés Node/npm, la opción publicada es:

   ```bash
   npm install -g @openai/codex
   ```

2. Abrí **Agregar primera cuenta**, asigná un nombre y elegí **Iniciar sesión con ChatGPT**.
3. Completá la autenticación en el navegador de OpenAI. No pegues contraseñas, tokens ni API keys en el widget.
4. Para agregar otra, elegí **Agregar nueva cuenta** en el selector y seleccioná la cuenta correcta en el navegador. Un inicio de sesión por vez.

El conector se detecta en `PATH`, en la instalación habitual de npm y en la ubicación independiente de Codex. Podés indicar un `codex.exe` o `codex.cmd` en Configuración. Se inicia un proceso breve por consulta, no un agente permanente por cuenta.

Cada cuenta tiene su propio `CODEX_HOME` bajo `%LOCALAPPDATA%\OpenAIUsageWidget\profiles\<id>`. Se exige `cli_auth_credentials_store="keyring"`, sin fallback a credenciales en texto plano. Según la versión de Codex, usa el Administrador de credenciales de Windows o archivos cifrados protegidos por él. No se alteran los perfiles habituales de Codex ni OpenCode.

**Compatibilidad:** la consulta usa el protocolo del cliente oficial de Codex, no una API pública de facturación con garantía de estabilidad. Mantené Codex CLI actualizado. Las ventanas se identifican por duración (300 y 10080 minutos), no por su posición en la respuesta. Un plan sin una de esas ventanas o una versión que no exponga el saldo muestra `—`; eso **no** significa cero. El plan/nombre real disponible puede variar; el nombre del panel es un alias local editable.

## Solo lectura

El transporte admite exclusivamente:

- `initialize` y la notificación de protocolo `initialized`.
- `account/read` y `account/rateLimits/read`.
- Autenticación: `account/login/start`, `account/login/cancel`, `account/logout`. Este último se usa únicamente al quitar un perfil propio del widget.

No admite turnos de modelos, herramientas, comandos remotos, compras ni canje de reseteos. Las solicitudes inversas del servidor se rechazan. No hay API keys ni credenciales en la configuración, las imágenes de prueba o los logs. Consultar la cuota no envía una solicitud a un modelo.

Si falla Internet o vence la sesión, se conserva la última lectura con advertencia. Reconectá desde Configuración. Un restablecimiento vencido **no** se convierte artificialmente en 100% restante: se vuelve a consultar al proveedor.

## Escritorio y Win+D

El panel conserva su posición relativa al host de iconos de Explorer, sin `Topmost`, sin inyectar código en Explorer y sin reemplazar el atajo Win+D. Se vuelve a detectar el host si cambia, incluyendo la jerarquía de Windows 11 24H2. Un evento de primer plano y una comprobación liviana cada 2 segundos recuperan la posición sin activar el panel.

**La jerarquía interna del escritorio no es un contrato estable de Microsoft.** Hay que validar Win+D en la compilación concreta de Windows; una actualización o un shell alternativo puede requerir ajustes. No se modifica el registro del sistema para corregirlo. La única entrada de registro opcional es el inicio automático del usuario, al activar expresamente esa casilla.

Comprobación manual:

1. Abrir el widget y una ventana normal encima: la ventana debe cubrirlo.
2. Presionar **Win+D**: debe verse el panel sobre el escritorio.
3. Restaurar las ventanas: deben seguir cubriendo el panel.
4. Abrir/cerrar el selector, moverlo, volver a iniciar y comprobar su posición.
5. Probar escalado de pantalla, varios monitores y escritorios virtuales si se utilizan.

Las pruebas automatizadas de escritorio no envían Win+D ni minimizan las ventanas del usuario. Es importante distinguirlas de esta comprobación manual.

## Datos locales

`%LOCALAPPDATA%\OpenAIUsageWidget\settings.json` guarda alias, email/identificador, cuenta seleccionada, posición, preferencias y última lectura. No contiene tokens. Las escrituras son atómicas. Una configuración ilegible se conserva y la app avisa; no se reemplaza silenciosamente.

Estos datos incluyen información personal de las cuentas: no los compartas ni los agregues a Git. Las credenciales están a cargo del almacenamiento protegido del conector, separado por perfil. Quitar una cuenta exige poder cerrar esa sesión propia con el conector.

## Pruebas y vista previa

La suite es un ejecutable de pruebas **sin dependencias NuGet de terceros**; devolviendo código distinto de cero si falla. Incluye un app-server simulado local para verificar autenticación, aislamiento, transporte y bloqueo de operaciones. No inicia sesión ni accede a OpenAI.

```powershell
dotnet run --project tests/OpenAIUsageWidget.Tests
```

La demostración usa datos **ficticios**, está rotulada, no conecta cuentas y no modifica la configuración real:

```powershell
.\run.ps1 -Demo
# O con el ejecutable ya compilado:
.\src\OpenAIUsageWidget\bin\Release\net10.0-windows\OpenAIUsageWidget.exe --demo
```

Diagnósticos offline, que terminan automáticamente:

```powershell
.\src\OpenAIUsageWidget\bin\Release\net10.0-windows\OpenAIUsageWidget.exe --preview artifacts\preview.png
.\src\OpenAIUsageWidget\bin\Release\net10.0-windows\OpenAIUsageWidget.exe --preview artifacts\empty.png --empty
.\src\OpenAIUsageWidget\bin\Release\net10.0-windows\OpenAIUsageWidget.exe --preview-dropdown artifacts\dropdown-preview.png
.\src\OpenAIUsageWidget\bin\Release\net10.0-windows\OpenAIUsageWidget.exe --smoke-test artifacts\desktop-smoke.json
```

## Publicar una carpeta portable

```powershell
dotnet publish src/OpenAIUsageWidget -c Release -r win-x64 --self-contained true -o artifacts\win-x64
```

Copiá **toda** la carpeta publicada. Esa distribución incluye .NET, pero el conector de cuentas sigue siendo una dependencia separada y explícita. No se incluye ni se descarga Codex en la publicación.

## Estructura

- `src/OpenAIUsageWidget.Core`: modelos, persistencia, traducción de cuotas y conector con lista de RPC permitidos.
- `src/OpenAIUsageWidget`: WPF, cuentas, configuración, temporizadores, bandeja y capa de escritorio.
- `tests/OpenAIUsageWidget.Tests`: pruebas de negocio, almacenamiento, interfaz de estado y transporte simulado.
- `main.png` / `dropdown.png`: referencias originales del diseño, conservadas sin modificar.

Licencia: Apache-2.0. Proyecto independiente, no afiliado a OpenAI.
