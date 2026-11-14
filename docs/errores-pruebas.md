# Errores encontrados en las pruebas (PC jenav, 12-13/11/2026)

1. Teclas pegadas (Ctrl y Alt izquierdas) tras cerrar o perder el control remoto: Alt + doble clic abria Propiedades en lugar de abrir carpetas. Causa: una copia sin el arreglo del commit 6e7b5d9. Solucion: cerrar TecLink y soltar teclas por software.
2. El teclado dejo de responder mientras TecLink estaba abierto (hook con Swallow activo en modo remoto, sin causa nueva en el log). Solucion: cerrar TecLink; abrir y cerrar la tapa de la laptop. Pendiente: salida de emergencia que libere teclado y mouse si no hay conexion o respuesta de la otra PC.
3. El build de desarrollo (net8.0-windows) no arranca donde solo hay .NET 10 ("You must install or update .NET"). Se lanzo con DOTNET_ROLL_FORWARD=Major. Pendiente: publicar autonomo (self-contained).
4. Sin conexion entre PCs: ping y puerto 51777 fallaban porque el servidor no estaba corriendo y la laptop estuvo sin internet. Se resolvio al abrir el servidor.
5. Marco amarillo en pantalla: no era TecLink (Herramienta Recortes / captura). Se quito al reiniciar la sesion.
6. error.log: ExternalException 0x800401D0 en ClipboardWatcher.WndProc (Clipboard.ContainsText) cuando otra app tiene el portapapeles abierto. Pendiente: reintentar o ignorar.

Novedad de esta rama: boton Encendido/Apagado (panel y bandeja) y switch de mando inmediato.

7. Causa raiz de las teclas pegadas (13/11/2026): al activar el modo remoto con Ctrl+Alt+Espacio el hook empezaba a tragarse tambien el KEYUP de Ctrl y Alt (y de un boton del mouse en un arrastre al cruzar), asi que Windows las daba por pulsadas. Corregido: el hook deja pasar el 'soltar' de toda tecla o boton que el sistema ya vio pulsar.
