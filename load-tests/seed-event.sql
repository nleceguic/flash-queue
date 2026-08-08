-- Evento fijo de 500 plazas usado por flashqueue-spike.js. Idempotente: reinicia el stock y
-- borra reservas previas de este mismo evento, así se puede volver a correr el test sin arrastrar
-- resultados de la ejecución anterior.
INSERT INTO events (id, name, total_stock, reserved_stock)
VALUES ('7c9e6679-7425-40de-944b-e07fc1f90ae7', 'FlashQueue Load Test - 500 plazas', 500, 0)
ON CONFLICT (id) DO UPDATE SET total_stock = 500, reserved_stock = 0;

DELETE FROM reservations WHERE event_id = '7c9e6679-7425-40de-944b-e07fc1f90ae7';
