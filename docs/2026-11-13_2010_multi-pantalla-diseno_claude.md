# TecLink: hasta 10 pantallas por usuario (diseno)

Meta pedida: cada usuario conecta hasta 10 pantallas. Prueba real: 3 PCs + una 4a pantalla HDMI.

## Situacion actual (0.5.0)
- Una sola conexion: `EdgeBridge._connection` (48 usos), `PeerServer.AcceptOneAsync`.
- El mapa espacial (`SpatialMap`) solo conoce dos anfitriones: local y remoto (`ScreenPlacement.Remote` es bool).
- Una pantalla extra HDMI en una PC ya funciona hoy como otra pantalla de ese anfitrion.

## Topologia elegida: estrella (servidor al centro)
- Un servidor; hasta 9 clientes. Cada cliente mantiene UNA conexion, solo con el servidor.
- El raton/teclado que pasa de cliente A a cliente C viaja A -> servidor -> C (el servidor reenvia).
- Ventajas: sin malla N x N, un solo puerto abierto (51777), un solo lugar donde vive el mapa.
- Costo: si el servidor se apaga, los clientes quedan aislados entre si.

## Cambios por capa
1. Protocolo (`WireMessage`): `Hello` lleva `PeerId` (guid fijo por PC) y `Displays`. Mensajes de entrada llevan `TargetPeerId` para que el servidor sepa a quien reenviar. Subir version de protocolo a 2.
2. Red (`PeerServer`): `AcceptManyAsync` con diccionario `PeerId -> PeerConnection`, maximo 10 anfitriones en total.
3. Mapa (`ScreenPlacement`): `Remote:bool` pasa a `HostId:string`. `SpatialMap.Build` recibe una lista de anfitriones con sus pantallas; las rutas entre cualquier par de bordes que se tocan.
4. `EdgeBridge`: `_connection` pasa a `_activePeer` (a quien controlo ahora) mas el registro de pares. Soltar/tomar mando se negocia con el servidor.
5. Panel: el mapa dibuja N anfitriones (color por PC), el boton "Cambiar de pantalla" recorre todas, la pestana Conexion lista cada PC con su estado.
6. Compatibilidad: 0.5.0 <-> 0.6.0 no se hablan (cambia el protocolo). Hay que actualizar todas las PCs a la vez.

## Fases (cada una se prueba sola)
- F1: red multiple + registro de PCs + panel lista (sin mover raton entre 3).
- F2: mapa de N anfitriones y rutas (pruebas de unidad en `TecLink.Regression`).
- F3: control entre 2 clientes pasando por el servidor + liberacion de teclas atascadas en cada salto.
- F4: pantalla HDMI extra y prueba con 3 PCs reales; limite de 10 y mensajes de error claros.

## Riesgos
- Teclas/botones atascados al saltar A -> C (ya hay `_osDown` y `_sentHeld*`; hay que liberar por par).
- Portapapeles y archivos: definir si van al par activo o a todos (propuesta: al par activo).
- Pruebas reales con 3 PCs: hoy solo hay 2 (jenav y rafae); se necesita la tercera.
