import java.net.URI
import java.security.MessageDigest
import org.gradle.api.artifacts.component.ModuleComponentIdentifier

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

// The two fonts are not kept in the repository. Each build fetches them from the commit of google/fonts that
// ../licenses/font-sources.json pins, and refuses a file whose SHA-256 differs from the one written there.
// A font already fetched and still matching is kept, so a later build needs no network.
abstract class FetchFonts : DefaultTask() {
    @get:InputFile abstract val sources: RegularFileProperty
    @get:OutputDirectory abstract val output: DirectoryProperty
    private fun sha256(bytes: ByteArray) =
        MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
    @TaskAction fun fetch() {
        @Suppress("UNCHECKED_CAST")
        val rows = groovy.json.JsonSlurper().parse(sources.get().asFile) as List<Map<String, Any?>>
        rows.forEach { row ->
            val resource = row["resource"] as String? ?: return@forEach
            val expected = row["sha256"] as String
            val target = output.get().file(resource).asFile
            if (target.isFile && sha256(target.readBytes()) == expected) return@forEach
            val bytes = URI(row["url"] as String).toURL().openStream().use { it.readBytes() }
            check(sha256(bytes) == expected) { "$resource: the fetched file does not match licenses/font-sources.json" }
            target.parentFile.mkdirs()
            target.writeBytes(bytes)
        }
    }
}
val fetchFonts = tasks.register<FetchFonts>("fetchFonts") {
    sources.set(rootProject.layout.projectDirectory.file("licenses/font-sources.json"))
    output.set(layout.buildDirectory.dir("fonts-res"))
}
android {
    namespace = "io.github.pattern_house_creative.rhythm.launcher"
    compileSdk = 36
    defaultConfig {
        applicationId = "io.github.pattern_house_creative.rhythm.launcher"
        minSdk = 28
        targetSdk = 36
        versionCode = 13
        versionName = "0.1.12-dev"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }
    buildTypes {
        create("fixture") {
            initWith(getByName("debug"))
            applicationIdSuffix = ".fixture"
            matchingFallbacks += listOf("debug")
        }
    }
    testBuildType = if (providers.gradleProperty("fixtureTests").isPresent) "fixture" else "debug"
    sourceSets.getByName("fixture").res.srcDir(layout.buildDirectory.dir("fixture-res"))
    sourceSets.getByName("main").res.srcDir(layout.buildDirectory.dir("fonts-res"))
    // The fonts' licences and where they came from travel inside the APK, as the OFL asks.
    sourceSets.getByName("main").assets.srcDir(rootProject.layout.projectDirectory.dir("licenses"))
    buildFeatures { compose = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    lint { abortOnError = true }
}
tasks.named("preBuild") { dependsOn(fetchFonts) }
// dependencies.txt lists every package that goes into the APK (runtime) and every other one its tests run with
// (test). docs/licenses/gradle.json is checked against that file without a build (node scripts/dependency-inventory.mjs),
// and CI checks the file against the build (checkDependencyList). After changing a version here:
// ./gradlew :launcher:writeDependencyList, then node scripts/collect-gradle-licenses.mjs.
fun dependencyList(): String {
    fun of(name: String) = configurations.findByName(name)?.incoming?.resolutionResult?.allComponents.orEmpty()
        .map { it.id }.filterIsInstance<ModuleComponentIdentifier>().map { "${it.group}:${it.module}:${it.version}" }.toSet()
    val runtime = of("releaseRuntimeClasspath")
    val test = of("debugUnitTestRuntimeClasspath") + of("debugAndroidTestRuntimeClasspath") - runtime
    return (runtime.map { "runtime $it" } + test.map { "test $it" }).sorted().joinToString("\n", postfix = "\n")
}
val dependencyListFile = layout.projectDirectory.file("dependencies.txt").asFile
tasks.register("writeDependencyList") { doLast { dependencyListFile.writeText(dependencyList()) } }
tasks.register("checkDependencyList") {
    doLast {
        check(dependencyListFile.readText().replace("\r\n", "\n") == dependencyList()) {
            "dependencies.txt is out of date: run ./gradlew :launcher:writeDependencyList, then node scripts/collect-gradle-licenses.mjs"
        }
    }
}
dependencies {
    implementation(platform("androidx.compose:compose-bom:2025.09.01"))
    implementation("androidx.activity:activity-compose:1.11.0")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.4")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    testImplementation("junit:junit:4.13.2")
    androidTestImplementation(platform("androidx.compose:compose-bom:2025.09.01"))
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
    androidTestImplementation("androidx.test:runner:1.7.0")
    debugImplementation("androidx.compose.ui:ui-test-manifest")
}
