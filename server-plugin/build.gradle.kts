plugins {
    java
    id("com.gradleup.shadow") version "9.6.1"
}

group = "dev.tabletopcraft"
version = "0.1.0"

java { toolchain { languageVersion.set(JavaLanguageVersion.of(25)) } }

repositories {
    mavenCentral()
    maven("https://repo.papermc.io/repository/maven-public/")
}

dependencies {
    compileOnly("io.papermc.paper:paper-api:26.3.build.181-stable")
    implementation("org.java-websocket:Java-WebSocket:1.6.0")
    compileOnly("com.google.code.gson:gson:2.11.0")
    testImplementation("com.google.code.gson:gson:2.11.0")
    testImplementation("io.papermc.paper:paper-api:26.3.build.181-stable")
    testImplementation(platform("org.junit:junit-bom:5.11.4"))
    testImplementation("org.junit.jupiter:junit-jupiter")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

tasks.test { useJUnitPlatform() }

tasks.processResources {
    val pluginVersion = project.version.toString()
    inputs.property("version", pluginVersion)
    filesMatching("plugin.yml") { expand("version" to pluginVersion) }
}

tasks.shadowJar {
    archiveClassifier.set("")
    relocate("org.java_websocket", "dev.tabletopcraft.ar.libs.websocket")
    dependencies { exclude(dependency("org.slf4j:.*")) }
    minimize()
}
tasks.build { dependsOn(tasks.shadowJar) }
