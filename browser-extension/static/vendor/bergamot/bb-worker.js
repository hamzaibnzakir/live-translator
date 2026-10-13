// Brainbox wrapper around Mozilla's unmodified bergamot translator-worker.js (MPL-2.0).
// Adds one RPC method so the extension can report WASM heap size in its performance stats.
importScripts('translator-worker.js');
/* global worker, Module */
worker.wasmMemoryBytes = function () {
  return (Module && Module.HEAP8 && Module.HEAP8.buffer.byteLength) || 0;
};
