# EvoSuite runtime

The bundled JAR is a reusable baseline asset. Any future experiment must pin
its exact version and SHA-256 in that experiment's own frozen protocol.

The standalone runtime JAR is retained for environments where EvoSuite's
dependencies are not available separately. EvoSuite is an external baseline;
its license and upstream notices must be preserved when redistributing this
directory. A compatible Java runtime is required at execution time.
