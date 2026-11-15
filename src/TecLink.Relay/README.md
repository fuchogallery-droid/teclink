# TecLink.Relay

Punto de encuentro para dos PCs que no se pueden alcanzar directamente (otra ciudad, un router que no deja entrar conexiones).
No tiene cuentas, ni correo, ni registros: solo une dos conexiones que llegan al mismo cuarto (un hash del secreto de la invitacion)
y copia bytes cifrados entre ellas. No puede leer ni cambiar nada: la llave nunca sale de las dos PCs.

- Puerto: variable PORT (8080 por defecto).  Salud: GET /health
- Sin TLS propio: va detras de Cloudflare o Railway, que ponen el certificado (los clientes usan wss).
- Limites: 5000 cuartos, 9 invitados por cuarto, 40 intentos por minuto por direccion.
- Docker: ver Dockerfile (contexto = raiz del repositorio).
